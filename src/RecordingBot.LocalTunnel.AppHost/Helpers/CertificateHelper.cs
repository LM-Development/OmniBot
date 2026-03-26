using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RecordingBot.LocalTunnel.AppHost.Helpers;

/// <summary>
/// Extracts TLS certificates from an AKS Kubernetes secret and
/// saves them as local PFX / PEM files for the bot to load directly.
/// </summary>
public static class CertificateHelper
{
    private static readonly string CertDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aks-tunnel", "certs");

    /// <summary>
    /// Extracts the TLS certificate from the Kubernetes secret, converts to PFX,
    /// and returns the absolute path to the PFX file.
    /// </summary>
    public static async Task<string> ExtractAsync(TunnelConfiguration config, CancellationToken ct = default)
    {
        Console.WriteLine("Extracting TLS certificate from AKS...");

        Directory.CreateDirectory(CertDir);

        // 1. Fetch secret data from Kubernetes
        var secretJson = await WslHelper.RunAsync(
            $"kubectl get secret {config.TlsSecretName} " +
            $"--context {config.KubeContext} " +
            $"-n {config.BotNamespace} " +
            $"-o json",
            ct);

        using var doc = JsonDocument.Parse(secretJson);
        var data = doc.RootElement.GetProperty("data");
        var certB64 = data.GetProperty("tls.crt").GetString()
            ?? throw new InvalidOperationException("tls.crt not found in secret.");
        var keyB64 = data.GetProperty("tls.key").GetString()
            ?? throw new InvalidOperationException("tls.key not found in secret.");

        // 2. Decode PEM content
        var certPem = Encoding.UTF8.GetString(Convert.FromBase64String(certB64));
        var keyPem = Encoding.UTF8.GetString(Convert.FromBase64String(keyB64));

        // Save PEM files for reference
        await File.WriteAllTextAsync(Path.Combine(CertDir, "tls.crt"), certPem, ct);
        await File.WriteAllTextAsync(Path.Combine(CertDir, "tls.key"), keyPem, ct);

        Console.WriteLine("  Certificate PEM files saved to " + CertDir);

        // 3. Create PFX from PEM
        using var cert = X509Certificate2.CreateFromPem(certPem, keyPem);
        var pfxBytes = cert.Export(X509ContentType.Pfx, "");
        var pfxPath = Path.Combine(CertDir, "tunnel-cert.pfx");
        await File.WriteAllBytesAsync(pfxPath, pfxBytes, ct);

        Console.WriteLine($"  PFX file saved to {pfxPath}");
        return pfxPath;
    }
}
