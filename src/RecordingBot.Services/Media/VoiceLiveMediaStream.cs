using Azure.AI.VoiceLive;
using Azure.Identity;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Skype.Bots.Media;
using NAudio.Wave;
using RecordingBot.Services.Contract;
using RecordingBot.Services.ServiceSetup;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RecordingBot.Services.Media
{
    /// <summary>
    /// Bridges a Teams call audio socket with Azure AI Voice Live.
    /// Incoming Teams audio (16 kHz PCM16 mono) is upsampled to 24 kHz and sent to the
    /// VoiceLive agent. Audio responses (24 kHz PCM16 mono) are downsampled back to
    /// 16 kHz and injected into the Teams call as 20 ms frames.
    /// </summary>
    public class VoiceLiveMediaStream : IMediaStream, IDisposable
    {
        // Teams audio format
        private const int TeamsSampleRate = 16000;
        // VoiceLive Pcm16 format uses 24 kHz
        private const int VoiceLiveSampleRate = 24000;
        private const int BitsPerSample = 16;
        private const int Channels = 1;
        // 20 ms frame = 320 samples × 2 bytes = 640 bytes at 16 kHz
        private const int FrameMs = 20;
        private static readonly int TeamsFrameBytes =
            TeamsSampleRate * FrameMs / 1000 * (BitsPerSample / 8) * Channels;

        private readonly VoiceLiveSettings _settings;
        private readonly IAudioSocket _audioSocket;
        private readonly IGraphLogger _logger;

        private VoiceLiveClient _client;
        private VoiceLiveSession _session;
        private readonly CancellationTokenSource _cts = new();

        private readonly SharePointService _sharePointService;

        private bool _sessionStarted;
        private bool _greetingSent;
        private bool _activeResponse;
        private bool _responseApiDone;
        private long _playbackTimestamp;
        private Dictionary<string, object> _pendingFunctionCall;

        // Audio queues
        private readonly BlockingCollection<byte[]> _sendQueue =
            new(new ConcurrentQueue<byte[]>());
        private readonly BlockingCollection<byte[]> _playbackQueue =
            new(new ConcurrentQueue<byte[]>());

        // Bytes from the last chunk that did not fill a complete 20 ms frame
        private byte[] _playbackRemainder = [];

        private Task _sendTask;
        private Task _eventTask;
        private Task _playbackTask;

        public VoiceLiveMediaStream(
            VoiceLiveSettings settings,
            IAudioSocket audioSocket,
            IGraphLogger logger,
            SharePointService sharePointService = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _audioSocket = audioSocket ?? throw new ArgumentNullException(nameof(audioSocket));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _sharePointService = sharePointService;
        }

        /// <summary>
        /// Called for every Teams audio frame. Starts the VoiceLive session on first call,
        /// then forwards the PCM data to the agent.
        /// </summary>
        public async Task AppendAudioBuffer(AudioMediaBuffer buffer, List<IParticipant> participants)
        {
            if (buffer.Timestamp == 0 || buffer.IsSilence ||
                buffer.Length == 0 || buffer.Data == IntPtr.Zero)
            {
                return;
            }

            if (!_sessionStarted)
                await StartSessionAsync().ConfigureAwait(false);

            // Copy Teams PCM (16 kHz 16-bit mono)
            var teamsAudio = new byte[buffer.Length];
            Marshal.Copy(buffer.Data, teamsAudio, 0, (int)buffer.Length);

            // Upsample 16 kHz → 24 kHz for VoiceLive
            var upsampled = Resample(teamsAudio, TeamsSampleRate, VoiceLiveSampleRate);
            _sendQueue.TryAdd(upsampled);
        }

        public async Task End()
        {
            _sendQueue.CompleteAdding();
            _playbackQueue.CompleteAdding();
            _cts.Cancel();

            try
            {
                if (_sendTask != null) await _sendTask.ConfigureAwait(false);
                if (_eventTask != null) await _eventTask.ConfigureAwait(false);
                if (_playbackTask != null) await _playbackTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }

            _session?.Dispose();
        }

        // ------------------------------------------------------------------ session start

        private async Task StartSessionAsync()
        {
            if (_sessionStarted) return;
            _sessionStarted = true;

            try
            {
                var credOptions = string.IsNullOrEmpty(_settings.AuthIdentityClientId)
                    ? null
                    : new DefaultAzureCredentialOptions
                      { ManagedIdentityClientId = _settings.AuthIdentityClientId };

                _client = new VoiceLiveClient(
                    new Uri(_settings.Endpoint),
                    new DefaultAzureCredential(credOptions));

                _session = await _client
                    .StartSessionAsync(_settings.Model, _cts.Token)
                    .ConfigureAwait(false);

                var options = new VoiceLiveSessionOptions
                {
                    InputAudioFormat = InputAudioFormat.Pcm16,
                    OutputAudioFormat = OutputAudioFormat.Pcm16,
                };
                if (!string.IsNullOrEmpty(_settings.Voice))
                    options.Voice = new AzureStandardVoice(_settings.Voice);
                if (!string.IsNullOrEmpty(_settings.Instructions))
                    options.Instructions = _settings.Instructions;

                // Register SharePoint tools when a service is configured
                if (_sharePointService is not null)
                {
                    var getUseCasesTool = new VoiceLiveFunctionDefinition("get_use_cases")
                    {
                        Description =
                            "Retrieves all use cases from the SharePoint list. " +
                            "Call this whenever the user asks about use cases, requirements, " +
                            "scenarios, or the contents of the use-case list.",
                        Parameters = BinaryData.FromObjectAsJson(new
                        {
                            type = "object",
                            properties = new { },
                            required = Array.Empty<string>()
                        })
                    };
                    options.Tools.Add(getUseCasesTool);

                    var schema = await _sharePointService.GetSchemaAsync(_cts.Token).ConfigureAwait(false);
                    options.Tools.Add(BuildCreateItemTool(schema));
                    options.ToolChoice = ToolChoiceLiteral.Auto;
                }

                await _session.ConfigureSessionAsync(options, _cts.Token).ConfigureAwait(false);

                _sendTask = Task.Run(ProcessSendQueueAsync);
                _eventTask = Task.Run(ProcessEventsAsync);
                _playbackTask = Task.Run(ProcessPlaybackQueueAsync);

                _logger.Info("VoiceLive session started.");
            }
            catch (Exception ex)
            {
                _sessionStarted = false;
                _logger.Error(ex, "Failed to start VoiceLive session.");
                throw;
            }
        }

        // ------------------------------------------------------------------ send Teams audio → VoiceLive

        private async Task ProcessSendQueueAsync()
        {
            try
            {
                foreach (var chunk in _sendQueue.GetConsumingEnumerable(_cts.Token))
                {
                    try
                    {
                        await _session.SendInputAudioAsync(chunk).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Error forwarding audio to VoiceLive.");
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        // ------------------------------------------------------------------ handle VoiceLive events

        private async Task ProcessEventsAsync()
        {
            try
            {
                await foreach (var update in _session
                    .GetUpdatesAsync(_cts.Token)
                    .ConfigureAwait(false))
                {
                    await HandleEventAsync(update).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error in VoiceLive event loop.");
            }
        }

        private async Task HandleEventAsync(SessionUpdate update)
        {
            switch (update)
            {
                case SessionUpdateSessionUpdated:
                    _logger.Info("VoiceLive session updated and ready.");
                    if (_settings.SendGreeting && !_greetingSent)
                    {
                        _greetingSent = true;
                        await SendGreetingAsync().ConfigureAwait(false);
                    }
                    break;

                case SessionUpdateResponseAudioDelta audioDelta:
                    if (audioDelta.Delta != null)
                        _playbackQueue.TryAdd(audioDelta.Delta.ToArray());
                    break;

                case SessionUpdateInputAudioBufferSpeechStarted:
                    // Barge-in: discard queued playback and cancel the active response
                    while (_playbackQueue.TryTake(out _)) { }
                    _playbackRemainder = [];
                    if (_activeResponse && !_responseApiDone)
                    {
                        try
                        {
                            await _session.CancelResponseAsync(_cts.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (
                            ex.Message?.Contains("no active response") == true) { }
                    }
                    break;

                case SessionUpdateResponseCreated:
                    _activeResponse = true;
                    _responseApiDone = false;
                    break;

                case SessionUpdateResponseFunctionCallArgumentsDone functionCallDone:
                    _pendingFunctionCall = new Dictionary<string, object>
                    {
                        ["name"] = functionCallDone.Name,
                        ["call_id"] = functionCallDone.CallId,
                        ["arguments"] = functionCallDone.Arguments
                    };
                    _logger.Info($"VoiceLive function call queued: {functionCallDone.Name}");
                    break;

                case SessionUpdateResponseDone:
                    _activeResponse = false;
                    _responseApiDone = true;
                    if (_pendingFunctionCall is not null)
                    {
                        await ExecuteFunctionCallAsync(_pendingFunctionCall).ConfigureAwait(false);
                        _pendingFunctionCall = null;
                    }
                    break;

                case SessionUpdateResponseAudioTranscriptDone transcriptDone:
                    _logger.Info($"VoiceLive agent: {transcriptDone.Transcript}");
                    break;

                case SessionUpdateConversationItemInputAudioTranscriptionCompleted transcription:
                    _logger.Info($"VoiceLive user: {transcription.Transcript}");
                    break;

                case SessionUpdateError errorEvent:
                    var msg = errorEvent.Error?.Message;
                    if (msg?.Contains("Cancellation failed: no active response") != true)
                        _logger.Error(new Exception(msg), "VoiceLive service error.");
                    break;
            }
        }

        // ------------------------------------------------------------------ inject VoiceLive audio → Teams

        private async Task ProcessPlaybackQueueAsync()
        {
            try
            {
                foreach (var chunk in _playbackQueue.GetConsumingEnumerable(_cts.Token))
                {
                    // Downsample 24 kHz → 16 kHz
                    var teamsAudio = Resample(chunk, VoiceLiveSampleRate, TeamsSampleRate);

                    // Prepend any leftover bytes that did not fill a frame last iteration
                    byte[] data;
                    if (_playbackRemainder.Length > 0)
                    {
                        data = new byte[_playbackRemainder.Length + teamsAudio.Length];
                        _playbackRemainder.CopyTo(data, 0);
                        teamsAudio.CopyTo(data, _playbackRemainder.Length);
                    }
                    else
                    {
                        data = teamsAudio;
                    }

                    int offset = 0;
                    while (offset + TeamsFrameBytes <= data.Length)
                    {
                        var frame = new byte[TeamsFrameBytes];
                        Array.Copy(data, offset, frame, 0, TeamsFrameBytes);
                        SendAudioFrame(frame);
                        offset += TeamsFrameBytes;

                        // Pace frames at real-time to avoid flooding the socket
                        await Task.Delay(FrameMs, _cts.Token).ConfigureAwait(false);
                    }

                    // Keep incomplete tail for the next chunk
                    _playbackRemainder = new byte[data.Length - offset];
                    if (_playbackRemainder.Length > 0)
                        Array.Copy(data, offset, _playbackRemainder, 0, _playbackRemainder.Length);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error in VoiceLive playback queue.");
            }
        }

        private void SendAudioFrame(byte[] frame)
        {
            var handle = GCHandle.Alloc(frame, GCHandleType.Pinned);
            try
            {
                using var buffer = new TeamsAudioSendBuffer(
                    handle.AddrOfPinnedObject(), frame.Length, _playbackTimestamp);
                _audioSocket.Send(buffer);

                // Advance by 20 ms expressed in 100 ns ticks
                _playbackTimestamp += FrameMs * 10_000L;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error injecting audio frame into Teams call.");
            }
            finally
            {
                handle.Free();
            }
        }

        // ------------------------------------------------------------------ SharePoint function calling

        private static VoiceLiveFunctionDefinition BuildCreateItemTool(IReadOnlyList<ListColumn> schema)
        {
            var properties = new Dictionary<string, object>();
            foreach (var col in schema)
            {
                var jsonType = col.Type switch
                {
                    "number" or "currency" => "number",
                    "boolean" => "boolean",
                    _ => "string"
                };

                var desc = string.IsNullOrWhiteSpace(col.Description)
                    ? $"{col.DisplayName} ({col.Type})"
                    : $"{col.DisplayName} ({col.Type}): {col.Description}";

                properties[col.InternalName] = new Dictionary<string, object>
                {
                    ["type"] = jsonType,
                    ["description"] = desc
                };
            }

            var requiredFields = schema
                .Where(c => c.Required)
                .Select(c => c.InternalName)
                .ToArray();

            var columnSummary = string.Join(", ",
                schema.Select(c => c.Required ? $"{c.DisplayName}*" : c.DisplayName));

            return new VoiceLiveFunctionDefinition("create_list_item")
            {
                Description =
                    "Creates a new item in the SharePoint use-case list. " +
                    "Before calling this function, ask the user for every required field (marked with *). " +
                    "Collect optional fields if the user provides them. " +
                    $"Fields (* = required): {columnSummary}",
                Parameters = BinaryData.FromObjectAsJson(new
                {
                    type = "object",
                    properties,
                    required = requiredFields
                })
            };
        }

        private async Task<string> CreateListItemFromCallAsync(Dictionary<string, object> call)
        {
            var arguments = (string)call["arguments"];
            using var doc = JsonDocument.Parse(arguments);

            var fields = new Dictionary<string, JsonElement>();
            foreach (var prop in doc.RootElement.EnumerateObject())
                fields[prop.Name] = prop.Value.Clone();

            return await _sharePointService.CreateListItemAsync(fields, _cts.Token).ConfigureAwait(false);
        }

        private async Task ExecuteFunctionCallAsync(Dictionary<string, object> call)
        {
            var name = (string)call["name"];
            var callId = (string)call["call_id"];

            _logger.Info($"VoiceLive executing function: {name}");
            string resultJson;

            try
            {
                resultJson = name switch
                {
                    "get_use_cases" when _sharePointService is not null =>
                        await _sharePointService.GetListItemsAsync(_cts.Token).ConfigureAwait(false),
                    "get_use_cases" =>
                        JsonSerializer.Serialize(new { error = "SharePoint service is not configured." }),
                    "create_list_item" when _sharePointService is not null =>
                        await CreateListItemFromCallAsync(call).ConfigureAwait(false),
                    "create_list_item" =>
                        JsonSerializer.Serialize(new { error = "SharePoint service is not configured." }),
                    _ =>
                        JsonSerializer.Serialize(new { error = $"Unknown function: {name}" })
                };
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"VoiceLive function {name} failed.");
                resultJson = JsonSerializer.Serialize(new { error = ex.Message });
            }

            await _session.AddItemAsync(new FunctionCallOutputItem(callId, resultJson), _cts.Token)
                .ConfigureAwait(false);

            await _session.StartResponseAsync(_cts.Token).ConfigureAwait(false);
            _logger.Info($"VoiceLive function {name} result sent.");
        }

        // ------------------------------------------------------------------ proactive greeting

        private async Task SendGreetingAsync()
        {
            try
            {
                await _session.SendCommandAsync(
                    BinaryData.FromObjectAsJson(new
                    {
                        type = "conversation.item.create",
                        item = new
                        {
                            type = "message",
                            role = "system",
                            content = new[]
                            {
                                new { type = "input_text", text = _settings.GreetingText }
                            }
                        }
                    }), _cts.Token).ConfigureAwait(false);

                await _session.SendCommandAsync(
                    BinaryData.FromObjectAsJson(new { type = "response.create" }),
                    _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to send VoiceLive greeting.");
            }
        }

        // ------------------------------------------------------------------ PCM resampling

        private static byte[] Resample(byte[] input, int fromRate, int toRate)
        {
            if (fromRate == toRate) return input;

            using var inputStream = new RawSourceWaveStream(
                new MemoryStream(input),
                new WaveFormat(fromRate, BitsPerSample, Channels));

            using var resampler = new MediaFoundationResampler(
                inputStream,
                new WaveFormat(toRate, BitsPerSample, Channels))
            {
                ResamplerQuality = 60,
            };

            using var output = new MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
                output.Write(buffer, 0, read);

            return output.ToArray();
        }

        // ------------------------------------------------------------------ concrete AudioMediaBuffer for sending

        /// <summary>
        /// Minimal concrete AudioMediaBuffer used to inject audio frames into the Teams call.
        /// The GCHandle pinning the audio data must remain live for the lifetime of this buffer.
        /// </summary>
        private sealed class TeamsAudioSendBuffer : AudioMediaBuffer
        {
            public TeamsAudioSendBuffer(IntPtr data, long length, long timestamp)
            {
                Data = data;
                Length = length;
                Timestamp = timestamp;
                AudioFormat = AudioFormat.Pcm16K;
            }

            protected override void Dispose(bool disposing) { }
        }

        // ------------------------------------------------------------------ IDisposable

        public void Dispose()
        {
            if (!_cts.IsCancellationRequested)
                _cts.Cancel();

            if (!_sendQueue.IsAddingCompleted)
                _sendQueue.CompleteAdding();

            if (!_playbackQueue.IsAddingCompleted)
                _playbackQueue.CompleteAdding();

            _session?.Dispose();
            _sendQueue.Dispose();
            _playbackQueue.Dispose();
            _cts.Dispose();
        }
    }
}
