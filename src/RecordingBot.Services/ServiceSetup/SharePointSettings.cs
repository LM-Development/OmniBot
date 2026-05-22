namespace RecordingBot.Services.ServiceSetup
{
    /// <summary>
    /// Configuration for SharePoint access via client-credentials.
    /// Bind from the "AzureSettings:SharePointSettings" configuration section or
    /// AzureSettings__SharePointSettings__ prefixed environment variables.
    /// </summary>
    public class SharePointSettings
    {
        /// <summary>Azure AD tenant ID.</summary>
        public string TenantId { get; set; } = string.Empty;

        /// <summary>App registration client ID.</summary>
        public string ClientId { get; set; } = string.Empty;

        /// <summary>App registration client secret.</summary>
        public string ClientSecret { get; set; } = string.Empty;

        /// <summary>SharePoint site ID (GUID or hostname,path form).</summary>
        public string SiteId { get; set; } = string.Empty;

        /// <summary>SharePoint list ID or display name.</summary>
        public string ListId { get; set; } = string.Empty;

        /// <summary>Returns true when all required settings are configured.</summary>
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(TenantId) &&
            !string.IsNullOrWhiteSpace(ClientId) &&
            !string.IsNullOrWhiteSpace(ClientSecret) &&
            !string.IsNullOrWhiteSpace(SiteId) &&
            !string.IsNullOrWhiteSpace(ListId);
    }
}
