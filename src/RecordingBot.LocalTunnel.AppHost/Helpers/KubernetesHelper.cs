using System;
using System.Threading;
using System.Threading.Tasks;

namespace RecordingBot.LocalTunnel.AppHost.Helpers;

/// <summary>
/// Queries Kubernetes resources via WSL kubectl.
/// </summary>
public static class KubernetesHelper
{
    /// <summary>
    /// Waits for the tunnel LoadBalancer service to be assigned an external IP.
    /// Returns the external IP address.
    /// </summary>
    public static async Task<string> WaitForLoadBalancerIpAsync(TunnelConfiguration config, CancellationToken ct = default)
    {
        Console.WriteLine("Waiting for LoadBalancer external IP...");

        for (int i = 0; i < 60; i++)
        {
            ct.ThrowIfCancellationRequested();

            var ip = await WslHelper.RunAsync(
                $"kubectl get svc {config.ReleaseName} " +
                $"--context {config.KubeContext} " +
                $"-n {config.Namespace} " +
                $"-o jsonpath='{{.status.loadBalancer.ingress[0].ip}}'",
                ct);

            if (!string.IsNullOrWhiteSpace(ip) && ip != "<pending>" && ip != "''")
            {
                // Strip surrounding quotes that jsonpath may leave
                ip = ip.Trim('\'', '"');
                if (!string.IsNullOrWhiteSpace(ip))
                {
                    Console.WriteLine($"  LoadBalancer IP: {ip}");
                    return ip;
                }
            }

            await Task.Delay(3000, ct);
        }

        throw new TimeoutException(
            "Timed out waiting for LoadBalancer IP. Check your AKS cluster and tunnel service.");
    }
}
