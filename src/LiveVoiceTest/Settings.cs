// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace LiveVoiceTest;

/// <summary>
/// Configuration for the Azure AI Voice Live service.
/// Bind from the "VoiceLive" configuration section or
/// VoiceLive__ prefixed environment variables.
/// </summary>
internal sealed class VoiceLiveOptions
{
    public const string Section = "VoiceLive";

    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Realtime model name, e.g. "gpt-realtime".</summary>
    public string Model { get; set; } = "gpt-4.1-mini";

    /// <summary>System instructions for the assistant.</summary>
    public string Instructions { get; set; } =
        "You are a helpful voice assistant. " +
        "When the user asks about use cases, scenarios, or requirements, " +
        "call the get_use_cases function to retrieve the list from SharePoint. " +
        "Always respond in the same language the user speaks.";

    /// <summary>TTS voice name.</summary>
    public string Voice { get; set; } = "de-AT-JonasNeural";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint);
}

/// <summary>
/// Configuration for SharePoint access via client-credentials.
/// Bind from the "SharePointFunctions" configuration section or
/// SharePointFunctions__ prefixed environment variables / user secrets.
/// </summary>
internal sealed class SharePointOptions
{
    public const string Section = "SharePointFunctions";

    /// <summary>Azure AD tenant ID.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>App registration client ID.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>App registration client secret.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>SharePoint site ID (GUID or hostname,path form).</summary>
    public string SiteId { get; set; } = string.Empty;

    /// <summary>SharePoint list ID or name.</summary>
    public string ListId { get; set; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(TenantId) &&
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret) &&
        !string.IsNullOrWhiteSpace(SiteId) &&
        !string.IsNullOrWhiteSpace(ListId);
}
