using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;

namespace RecordingBot.LocalTunnel.AppHost;

public sealed class TunnelConfiguration
{
    [Required]
    public string KubeContext { get; set; } = "";

    public string Namespace { get; set; } = "dev-tunnel";

    public string ReleaseName { get; set; } = "local-dev-tunnel";

    [Required]
    public string BotReleaseName { get; set; } = "teams-recording-bot";

    public string BotNamespace { get; set; } = "default";

    [Required]
    public string Host { get; set; } = "";

    public int PublicHttpsPort { get; set; } = 443;

    public int PublicMediaPort { get; set; } = 28551;

    public int SignalingPort { get; set; } = 9441;

    public int MediaPort { get; set; } = 8445;

    public int LocalSshPort { get; set; } = 2222;

    public string? PublicIp { get; set; }

    [Required]
    public string ChartPath { get; set; } = "../../deploy/local-dev-tunnel";

    public string IngressClassName { get; set; } = "traefik";

    /// <summary>
    /// Path prefix for this developer's tunnel, e.g. "/dev-john" or "/tunnel-mary".
    /// This allows multiple developers to tunnel simultaneously and differentiates
    /// from the production bot deployment.
    /// </summary>
    [Required]
    public string DeveloperPathPrefix { get; set; } = "";

    /// <summary>
    /// Resolves the TLS secret name using the same naming convention as the main chart.
    /// </summary>
    public string TlsSecretName => $"ingress-tls-{BotReleaseName}";

    public void Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(KubeContext) || KubeContext == "YOUR_AKS_CONTEXT")
            errors.Add("Tunnel:KubeContext must be set to your AKS kubectl context name.");

        if (string.IsNullOrWhiteSpace(Host) || Host == "YOUR_BOT_FQDN")
            errors.Add("Tunnel:Host must be set to your bot's FQDN (e.g. bot.example.com).");

        if (string.IsNullOrWhiteSpace(ChartPath))
            errors.Add("Tunnel:ChartPath must point to the local-dev-tunnel Helm chart directory.");

        if (string.IsNullOrWhiteSpace(DeveloperPathPrefix))
            errors.Add("Tunnel:DeveloperPathPrefix must be set (e.g. '/dev-yourname') to differentiate from production and other developers.");

        if (!string.IsNullOrWhiteSpace(DeveloperPathPrefix) && !DeveloperPathPrefix.StartsWith("/"))
            errors.Add("Tunnel:DeveloperPathPrefix must start with '/' (e.g. '/dev-yourname').");

        if (errors.Count > 0)
            throw new InvalidOperationException(
                "Tunnel configuration is invalid:\n" + string.Join("\n", errors.Select(e => $"  - {e}")));
    }
}
