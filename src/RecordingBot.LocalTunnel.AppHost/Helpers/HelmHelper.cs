using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RecordingBot.LocalTunnel.AppHost.Helpers;

/// <summary>
/// Deploys and manages the local-dev-tunnel Helm chart via WSL kubectl/helm.
/// </summary>
public static class HelmHelper
{
    /// <summary>
    /// Deploys (or upgrades) the tunnel Helm chart to the AKS cluster.
    /// </summary>
    public static async Task DeployAsync(TunnelConfiguration config, string sshPublicKey, CancellationToken ct = default)
    {
        Console.WriteLine("Deploying local-dev-tunnel Helm chart...");

        var chartPath = WslHelper.ToWslPath(Path.GetFullPath(config.ChartPath));

        var setArgs = new List<string>
        {
            $"--set ports.signaling={config.SignalingPort}",
            $"--set ports.media={config.MediaPort}",
            $"--set service.mediaPort={config.PublicMediaPort}",
            $"--set ingress.host={config.Host}",
            $"--set ingress.className={config.IngressClassName}",
            $"--set ingress.path={config.DeveloperPathPrefix}",
            $"--set ingress.botReleaseName={config.BotReleaseName}",
        };

        if (!string.IsNullOrWhiteSpace(config.PublicIp))
            setArgs.Add($"--set service.publicIp={config.PublicIp}");

        var cmd =
            $"helm upgrade {config.ReleaseName} {chartPath} " +
            $"--kube-context {config.KubeContext} " +
            $"--namespace {config.Namespace} --create-namespace " +
            $"--install --wait --timeout 120s " +
            string.Join(" ", setArgs);

        await WslHelper.RunAsync(cmd, ct);

        Console.WriteLine("  Helm chart deployed.");
    }

    /// <summary>
    /// Uninstalls the tunnel Helm chart.
    /// </summary>
    public static async Task UninstallAsync(TunnelConfiguration config, CancellationToken ct = default)
    {
        Console.WriteLine("Uninstalling local-dev-tunnel Helm chart...");
        await WslHelper.RunAsync(
            $"helm uninstall {config.ReleaseName} --kube-context {config.KubeContext} --namespace {config.Namespace}",
            ct);
    }
}
