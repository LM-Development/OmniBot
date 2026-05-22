using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Common;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Skype.Bots.Media;
using RecordingBot.Services.Contract;
using RecordingBot.Services.Media;
using RecordingBot.Services.ServiceSetup;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RecordingBot.Services.Bot
{
    /// <summary>
    /// Class responsible for streaming audio and video.
    /// When VoiceLive is configured in AzureSettings, audio is routed through the
    /// Voice Live agent and the agent's spoken responses are injected back into the call.
    /// Otherwise the bot records audio to disk as before.
    /// </summary>
    public class BotMediaStream : ObjectRootDisposable
    {
        internal List<IParticipant> participants;
        private readonly IAudioSocket _audioSocket;
        private readonly IMediaStream _mediaStream;
        private readonly IEventPublisher _eventPublisher;
        private readonly string _callId;
        public SerializableAudioQualityOfExperienceData AudioQualityOfExperienceData { get; private set; }

        public BotMediaStream(
            ILocalMediaSession mediaSession,
            string callId,
            IGraphLogger logger,
            IEventPublisher eventPublisher,
            IAzureSettings settings) : base(logger)
        {
            ArgumentNullException.ThrowIfNull(mediaSession, nameof(mediaSession));
            ArgumentNullException.ThrowIfNull(logger, nameof(logger));
            ArgumentNullException.ThrowIfNull(settings, nameof(settings));

            participants = [];

            _eventPublisher = eventPublisher;
            _callId = callId;

            // Subscribe to the audio media.
            _audioSocket = mediaSession.AudioSocket;
            if (_audioSocket == null)
            {
                throw new InvalidOperationException("A mediaSession needs to have at least an audioSocket");
            }

            // Use VoiceLiveMediaStream when VoiceLive is configured; fall back to WAV recording.
            var azureSettings = (AzureSettings)settings;
            if (azureSettings.VoiceLiveSettings?.IsConfigured == true)
            {
                SharePointService sharePointService = null;
                if (azureSettings.SharePointSettings?.IsConfigured == true)
                    sharePointService = new SharePointService(azureSettings.SharePointSettings);

                _mediaStream = new VoiceLiveMediaStream(
                    azureSettings.VoiceLiveSettings, _audioSocket, logger, sharePointService);
            }
            else
            {
                _mediaStream = new MediaStream(settings, logger, mediaSession.MediaSessionId.ToString());
            }

            _audioSocket.AudioMediaReceived += OnAudioMediaReceived;
        }

        public List<IParticipant> GetParticipants()
        {
            return participants;
        }

        public SerializableAudioQualityOfExperienceData GetAudioQualityOfExperienceData()
        {
            AudioQualityOfExperienceData = new SerializableAudioQualityOfExperienceData(_callId, _audioSocket.GetQualityOfExperienceData());
            return AudioQualityOfExperienceData;
        }

        public async Task StopMedia()
        {
            await _mediaStream.End();
            // Event - Stop media occurs when the call stops recording
            _eventPublisher.Publish("StopMediaStream", "Call stopped recording");
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            // Event Dispose of the bot media stream object
            _eventPublisher.Publish("MediaStreamDispose", disposing.ToString());

            base.Dispose(disposing);

            _audioSocket.AudioMediaReceived -= OnAudioMediaReceived;

            if (_mediaStream is IDisposable disposableStream)
                disposableStream.Dispose();
        }

        private async void OnAudioMediaReceived(object sender, AudioMediaReceivedEventArgs e)
        {
            GraphLogger.Verbose($"Received Audio: [AudioMediaReceivedEventArgs(Data=<{e.Buffer.Data}>, Length={e.Buffer.Length}, Timestamp={e.Buffer.Timestamp})]");

            try
            {
                await _mediaStream.AppendAudioBuffer(e.Buffer, participants);
                e.Buffer.Dispose();
            }
            catch (Exception ex)
            {
                GraphLogger.Error(ex);
            }
            finally
            {
                e.Buffer.Dispose();
            }
        }
    }
}
