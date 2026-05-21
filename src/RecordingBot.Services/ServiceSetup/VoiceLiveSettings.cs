namespace RecordingBot.Services.ServiceSetup
{
    /// <summary>
    /// Configuration for the Azure AI Voice Live integration.
    /// Bind with the section "AzureSettings:VoiceLiveSettings" or environment variables
    /// using the AzureSettings__VoiceLiveSettings__ prefix.
    /// </summary>
    public class VoiceLiveSettings
    {
        /// <summary>Voice Live service endpoint URI.</summary>
        public string Endpoint { get; set; }

        /// <summary>Foundry agent name / ID.</summary>
        public string AgentId { get; set; }

        /// <summary>Foundry project name.</summary>
        public string ProjectName { get; set; }

        /// <summary>Optional specific agent version.</summary>
        public string AgentVersion { get; set; }

        /// <summary>Optional TTS voice name (e.g. "en-US-Ava:DragonHDLatestNeural").</summary>
        public string Voice { get; set; }

        /// <summary>Optional cross-resource Foundry endpoint override.</summary>
        public string FoundryResourceOverride { get; set; }

        /// <summary>Optional managed identity client ID for cross-resource authentication.</summary>
        public string AuthIdentityClientId { get; set; }

        /// <summary>When true, a greeting message is sent to the agent at session start.</summary>
        public bool SendGreeting { get; set; } = true;

        /// <summary>Greeting instruction sent to the agent on session start.</summary>
        public string GreetingText { get; set; } = "Say something to welcome the user.";

        /// <summary>Returns true when the minimum required settings are configured.</summary>
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(Endpoint) &&
            !string.IsNullOrWhiteSpace(AgentId) &&
            !string.IsNullOrWhiteSpace(ProjectName);
    }
}
