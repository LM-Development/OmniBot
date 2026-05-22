// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Azure.AI.VoiceLive;
using Azure.Identity;
using LiveVoiceTest;
using Microsoft.Extensions.Configuration;
using NAudio.Wave;
using System.Collections.Concurrent;
using System.Text.Json;

// <all>
/// <summary>
/// Voice assistant using Azure AI Voice Live SDK with Foundry Agent support.
///
/// This sample demonstrates:
/// - Connecting to Voice Live with AgentSessionConfig via SessionTarget.FromAgent()
/// - Configuring interim responses to bridge latency gaps
/// - Proactive greeting message on session start
/// - Real-time audio capture and playback with barge-in support
/// - Conversation logging to a file
///
/// Required environment variables:
///   AZURE_VOICELIVE_ENDPOINT       - Voice Live service endpoint
///   AZURE_VOICELIVE_AGENT_ID       - Name of the Foundry agent
///   AZURE_VOICELIVE_PROJECT_NAME   - Foundry project name (e.g., myproject)
///
/// Optional environment variables:
///   AZURE_VOICELIVE_VOICE                         - Voice name (default: en-US-Ava:DragonHDLatestNeural)
///   AZURE_VOICELIVE_AGENT_VERSION                 - Specific agent version
///   AZURE_VOICELIVE_CONVERSATION_ID               - Resume a previous conversation
///   AZURE_VOICELIVE_FOUNDRY_RESOURCE_OVERRIDE     - Cross-resource Foundry endpoint
///   AZURE_VOICELIVE_AUTH_IDENTITY_CLIENT_ID       - Managed identity client ID for cross-resource auth
/// </summary>

// <audio_processor>
/// <summary>
/// Manages real-time audio capture from the microphone and playback to the speakers.
/// Uses a blocking collection for audio buffering and supports barge-in (skip pending audio).
/// </summary>
class AudioProcessor : IDisposable
{
    private readonly VoiceLiveSession _session;
    private const int SampleRate = 24000;
    private const int BitsPerSample = 16;
    private const int Channels = 1;

    private WaveInEvent? _waveIn;
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _playbackBuffer;

    private readonly BlockingCollection<byte[]> _sendQueue = new(new ConcurrentQueue<byte[]>());
    private readonly BlockingCollection<byte[]> _playbackQueue = new(new ConcurrentQueue<byte[]>());
    private CancellationTokenSource _playbackCts = new();
    private Task? _sendTask;
    private Task? _playbackTask;
    private bool _isCapturing;

    public AudioProcessor(VoiceLiveSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public void StartCapture()
    {
        if (_isCapturing) return;
        _isCapturing = true;

        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels),
            BufferMilliseconds = 50
        };

        _waveIn.DataAvailable += (sender, e) =>
        {
            if (e.BytesRecorded > 0 && _isCapturing)
            {
                var audioData = new byte[e.BytesRecorded];
                Array.Copy(e.Buffer, audioData, e.BytesRecorded);
                _sendQueue.TryAdd(audioData);
            }
        };

        _waveIn.StartRecording();
        _sendTask = Task.Run(ProcessSendQueueAsync);
        Console.WriteLine("🎤 Audio capture started");
    }

    public void StartPlayback()
    {
        _playbackBuffer = new BufferedWaveProvider(new WaveFormat(SampleRate, BitsPerSample, Channels))
        {
            BufferDuration = TimeSpan.FromSeconds(10),
            DiscardOnBufferOverflow = true
        };

        _waveOut = new WaveOutEvent { DesiredLatency = 100 };
        _waveOut.Init(_playbackBuffer);
        _waveOut.Play();

        _playbackCts = new CancellationTokenSource();
        _playbackTask = Task.Run(() => ProcessPlaybackQueue(_playbackCts.Token));
    }

    public void QueueAudio(byte[] audioData)
    {
        if (audioData.Length > 0)
        {
            _playbackQueue.TryAdd(audioData);
        }
    }

    public void SkipPendingAudio()
    {
        // Clear queued audio for barge-in
        while (_playbackQueue.TryTake(out _)) { }
        _playbackBuffer?.ClearBuffer();
    }

    private async Task ProcessSendQueueAsync()
    {
        try
        {
            foreach (var audioData in _sendQueue.GetConsumingEnumerable())
            {
                try
                {
                    await _session.SendInputAudioAsync(audioData).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error sending audio: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void ProcessPlaybackQueue(CancellationToken ct)
    {
        try
        {
            foreach (var audioData in _playbackQueue.GetConsumingEnumerable(ct))
            {
                _playbackBuffer?.AddSamples(audioData, 0, audioData.Length);
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _isCapturing = false;
        _sendQueue.CompleteAdding();
        _playbackCts.Cancel();

        _waveIn?.StopRecording();
        _waveIn?.Dispose();
        _waveOut?.Stop();
        _waveOut?.Dispose();

        _sendTask?.Wait(TimeSpan.FromSeconds(2));
        _playbackTask?.Wait(TimeSpan.FromSeconds(2));

        _sendQueue.Dispose();
        _playbackQueue.Dispose();
        _playbackCts.Dispose();
    }
}
// </audio_processor>

// <voice_assistant>
/// <summary>
/// Voice assistant that connects to a Foundry Agent via the Voice Live service.
/// Handles session lifecycle, event processing, and audio I/O.
/// </summary>
class BasicVoiceAssistant : IDisposable
{
    private readonly string _endpoint;
    private readonly string _model;
    private readonly string _instructions;
    private readonly string _voice;
    private readonly SharePointService? _sharePointService;
    private VoiceLiveSession? _session;
    private AudioProcessor? _audioProcessor;
    private bool _greetingSent;
    private bool _activeResponse;
    private bool _responseApiDone;
    private Dictionary<string, object>? _pendingFunctionCall;

    // Conversation log
    private static readonly string LogFilename = $"conversation_{DateTime.Now:yyyyMMdd_HHmmss}.log";

    // <agent_config>
    public BasicVoiceAssistant(VoiceLiveOptions opts, SharePointService? sharePointService = null)
    {
        _endpoint = opts.Endpoint;
        _model = opts.Model;
        _instructions = opts.Instructions;
        _voice = opts.Voice;
        _sharePointService = sharePointService;
    }
    // </agent_config>

    // <start_session>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine("Connecting to VoiceLive API with agent config...");

        // Create the Voice Live client with Entra ID authentication
        var client = new VoiceLiveClient(
            new Uri(_endpoint),
            new DefaultAzureCredential());

        // Connect using the model name so tools and instructions can be set at runtime
        _session = await client.StartSessionAsync(_model, cancellationToken).ConfigureAwait(false);

        try
        {
            _audioProcessor = new AudioProcessor(_session);

            // Configure session options
            await SetupSessionAsync(cancellationToken).ConfigureAwait(false);

            _audioProcessor.StartPlayback();

            Console.WriteLine();
            Console.WriteLine(new string('=', 65));
            Console.WriteLine("🎤 VOICE ASSISTANT READY");
            Console.WriteLine("Start speaking to begin conversation");
            Console.WriteLine("Press Ctrl+C to exit");
            Console.WriteLine(new string('=', 65));
            Console.WriteLine();

            // Process events (blocking)
            await ProcessEventsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error during session: {ex.Message}");
            throw new Exception("Error during session", ex);
        }
        finally
        {
            _audioProcessor?.Dispose();
            _session?.Dispose();
        }
    }
    // </start_session>

    // <setup_session>
    private async Task SetupSessionAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("Setting up voice conversation session...");

        var interimConfig = new LlmInterimResponseConfig
        {
            Instructions = "Create friendly interim responses indicating wait time due to "
                + "ongoing processing, if any. Do not include in all responses! Do not "
                + "say you don't have real-time access to information when calling tools!",
        };
        interimConfig.Triggers.Add(InterimResponseTrigger.Tool);
        interimConfig.Triggers.Add(InterimResponseTrigger.Latency);
        interimConfig.LatencyThresholdMs = 100;

        var options = new VoiceLiveSessionOptions
        {
            Model = _model,
            Instructions = _instructions,
            InputAudioFormat = InputAudioFormat.Pcm16,
            OutputAudioFormat = OutputAudioFormat.Pcm16,
            //InterimResponse = BinaryData.FromObjectAsJson(interimConfig),
            Voice = new AzureStandardVoice(_voice),
            InputAudioTranscription = new AudioInputTranscriptionOptions(
                AudioInputTranscriptionOptionsModel.AzureSpeech),
           TurnDetection = new AzureSemanticVadTurnDetection()
           {
               RemoveFillerWords = true
           }
        };

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
        options.ToolChoice = ToolChoiceLiteral.Auto;

        await _session!.ConfigureSessionAsync(options, cancellationToken).ConfigureAwait(false);
        Console.WriteLine("Session configuration sent");
    }
    // </setup_session>

    // <process_events>
    private async Task ProcessEventsAsync(CancellationToken cancellationToken)
    {
        await foreach (SessionUpdate serverEvent in _session!.GetUpdatesAsync(cancellationToken).ConfigureAwait(false))
        {
            await HandleEventAsync(serverEvent, cancellationToken).ConfigureAwait(false);
        }
    }
    // </process_events>

    // <handle_events>
    private async Task HandleEventAsync(SessionUpdate serverEvent, CancellationToken cancellationToken)
    {
        switch (serverEvent)
        {
            case SessionUpdateSessionUpdated sessionUpdated:
                Console.WriteLine("Session updated and ready");

                var sessionId = sessionUpdated.Session?.Id;
                WriteLog($"SessionID: {sessionId}\n");

                // Send a proactive greeting
                if (!_greetingSent)
                {
                    _greetingSent = true;
                    await SendProactiveGreetingAsync(cancellationToken).ConfigureAwait(false);
                }

                // Start audio capture once session is ready
                _audioProcessor?.StartCapture();
                break;

            case SessionUpdateConversationItemInputAudioTranscriptionCompleted transcription:
                var userText = transcription.Transcript;
                Console.WriteLine($"👤 You said:\t{userText}");
                WriteLog($"User Input:\t{userText}");
                break;

            case SessionUpdateResponseAudioTranscriptDone audioTranscriptDone:
                var agentText = audioTranscriptDone.Transcript;
                Console.WriteLine($"🤖 Agent responded:\t{agentText}");
                WriteLog($"Agent Audio Response:\t{agentText}");
                break;

            case SessionUpdateInputAudioBufferSpeechStarted:
                Console.WriteLine("🎤 Listening...");
                _audioProcessor?.SkipPendingAudio();

                // Cancel in-progress response for barge-in
                if (_activeResponse && !_responseApiDone)
                {
                    try
                    {
                        await _session!.CancelResponseAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex.Message?.Contains("no active response") == true)
                    {
                        // Benign - response already completed
                    }
                }
                break;

            case SessionUpdateInputAudioBufferSpeechStopped:
                Console.WriteLine("🤔 Processing...");
                break;

            case SessionUpdateResponseCreated:
                _activeResponse = true;
                _responseApiDone = false;
                break;

            case SessionUpdateResponseAudioDelta audioDelta:
                if (audioDelta.Delta != null)
                {
                    _audioProcessor?.QueueAudio(audioDelta.Delta.ToArray());
                }
                break;

            case SessionUpdateResponseAudioDone:
                Console.WriteLine("🎤 Ready for next input...");
                break;

            case SessionUpdateResponseDone:
                _activeResponse = false;
                _responseApiDone = true;
                if (_pendingFunctionCall is not null)
                {
                    await ExecuteFunctionCallAsync(_pendingFunctionCall, cancellationToken).ConfigureAwait(false);
                    _pendingFunctionCall = null;
                }
                break;

            case SessionUpdateResponseFunctionCallArgumentsDone functionCallDone:
                _pendingFunctionCall = new Dictionary<string, object>
                {
                    ["name"] = functionCallDone.Name,
                    ["call_id"] = functionCallDone.CallId,
                    ["arguments"] = functionCallDone.Arguments
                };
                Console.WriteLine($"⚙️  Function call queued: {functionCallDone.Name}");
                break;

            case SessionUpdateError errorEvent:
                var errorMsg = errorEvent.Error?.Message;
                if (errorMsg?.Contains("Cancellation failed: no active response") == true)
                {
                    // Benign cancellation error
                }
                else
                {
                    Console.Error.WriteLine($"VoiceLive error: {errorMsg}");
                }
                break;
        }
    }
    // </handle_events>

    // <function_calling>
    private async Task ExecuteFunctionCallAsync(Dictionary<string, object> call, CancellationToken cancellationToken)
    {
        var name = (string)call["name"];
        var callId = (string)call["call_id"];

        Console.WriteLine($"⚙️  Executing function: {name}");
        string resultJson;

        try
        {
            resultJson = name switch
            {
                "get_use_cases" when _sharePointService is not null =>
                    await _sharePointService.GetListItemsAsync(cancellationToken).ConfigureAwait(false),
                "get_use_cases" =>
                    JsonSerializer.Serialize(new { error = "SharePoint service is not configured." }),
                _ =>
                    JsonSerializer.Serialize(new { error = $"Unknown function: {name}" })
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Function {name} failed: {ex.Message}");
            resultJson = JsonSerializer.Serialize(new { error = ex.Message });
        }

        await _session!.AddItemAsync(new FunctionCallOutputItem(callId, resultJson), cancellationToken)
            .ConfigureAwait(false);

        await _session!.StartResponseAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"✅ Function {name} result sent");
    }
    // </function_calling>

    // <proactive_greeting>
    private async Task SendProactiveGreetingAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("Sending proactive greeting request");
        try
        {
            // Create a system message to trigger greeting
            await _session!.SendCommandAsync(
                BinaryData.FromObjectAsJson(new
                {
                    type = "conversation.item.create",
                    item = new
                    {
                        type = "message",
                        role = "system",
                        content = new[]
                        {
                            new { type = "input_text", text = "Say something to welcome the user in English." }
                        }
                    }
                }), cancellationToken).ConfigureAwait(false);

            // Request a response
            await _session!.SendCommandAsync(
                BinaryData.FromObjectAsJson(new { type = "response.create" }),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to send proactive greeting: {ex.Message}");
        }
    }
    // </proactive_greeting>

    private static void WriteLog(string message)
    {
        try
        {
            var logDir = Path.Combine(Directory.GetCurrentDirectory(), "logs");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, LogFilename), message + Environment.NewLine);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Failed to write conversation log: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _audioProcessor?.Dispose();
        _session?.Dispose();
    }
}
// </voice_assistant>

// <main>
class Program
{
    static async Task Main(string[] args)
    {
        // Build configuration: environment variables override, user secrets for secrets
        IConfiguration config = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddUserSecrets<Program>()
            .Build();

        var voiceLiveOpts = new VoiceLiveOptions();
        config.GetSection(VoiceLiveOptions.Section).Bind(voiceLiveOpts);

        var sharePointOpts = new SharePointOptions();
        config.GetSection(SharePointOptions.Section).Bind(sharePointOpts);

        Console.WriteLine("Configuration:");
        Console.WriteLine($"  VoiceLive Endpoint:   {voiceLiveOpts.Endpoint}");
        Console.WriteLine($"  VoiceLive Model:      {voiceLiveOpts.Model}");
        Console.WriteLine($"  VoiceLive Voice:      {voiceLiveOpts.Voice}");
        Console.WriteLine($"  SharePoint configured:{sharePointOpts.IsConfigured}");

        if (!voiceLiveOpts.IsConfigured)
        {
            Console.Error.WriteLine(
                "Set VoiceLive__Endpoint via environment variables or appsettings.");
            return;
        }

        // Verify audio devices
        CheckAudioDevices();

        Console.WriteLine("🎙️ Basic Foundry Voice Agent with Azure VoiceLive SDK (Agent Mode)");
        Console.WriteLine(new string('=', 65));

        SharePointService? spService = sharePointOpts.IsConfigured
            ? new SharePointService(sharePointOpts)
            : null;

        using var assistant = new BasicVoiceAssistant(voiceLiveOpts, spService);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            await assistant.StartAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\n👋 Voice assistant shut down. Goodbye!");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal Error: {ex.Message}");
        }
    }

    // <check_audio>
    static void CheckAudioDevices()
    {
        if (WaveInEvent.DeviceCount == 0)
        {
            Console.Error.WriteLine("❌ No audio input devices found. Please check your microphone.");
            Environment.Exit(1);
        }
        try
        {
            using var testOut = new WaveOutEvent();
        }
        catch
        {
            Console.Error.WriteLine("❌ No audio output devices found. Please check your speakers.");
            Environment.Exit(1);
        }
    }
    // </check_audio>
}
// </main>
// </all>
