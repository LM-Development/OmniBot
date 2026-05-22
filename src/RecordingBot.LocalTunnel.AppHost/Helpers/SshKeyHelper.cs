using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RecordingBot.LocalTunnel.AppHost.Helpers;

/// <summary>
/// Generates and manages an SSH key pair used for the reverse tunnel.
/// Keys are stored at ~/.aks-tunnel/ on the Windows side and accessed
/// from WSL via the /mnt/c/ mount.
/// </summary>
public static class SshKeyHelper
{
    private static readonly string KeyDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aks-tunnel");

    public static string PrivateKeyPath => Path.Combine(KeyDir, "id_ed25519");
    public static string PublicKeyPath => PrivateKeyPath + ".pub";

    /// <summary>
    /// Ensures an SSH key pair exists, generating one if needed.
    /// Returns the Windows path to the private key.
    /// </summary>
    public static async Task<string> EnsureKeyPairAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(KeyDir);

        if (File.Exists(PrivateKeyPath) && File.Exists(PublicKeyPath))
        {
            Console.WriteLine("  SSH key pair already exists.");
            return PrivateKeyPath;
        }

        Console.WriteLine("  Generating SSH key pair for tunnel...");

        var wslKeyPath = WslHelper.ToWslPath(PrivateKeyPath);

        // Remove any stale key first
        await WslHelper.RunAsync($"rm -f {wslKeyPath} {wslKeyPath}.pub", ct);
        await WslHelper.RunAsync($"ssh-keygen -t ed25519 -f {wslKeyPath} -N '' -q", ct);

        // Fix permissions from WSL side (Windows-mounted files default to 0777)
        await WslHelper.RunAsync($"chmod 600 {wslKeyPath}", ct);

        if (!File.Exists(PrivateKeyPath))
            throw new FileNotFoundException("SSH key generation succeeded in WSL but file not found on Windows side.", PrivateKeyPath);

        Console.WriteLine($"  SSH key pair written to {KeyDir}");
        return PrivateKeyPath;
    }

    /// <summary>
    /// Reads the public key content for embedding in Helm values.
    /// </summary>
    public static async Task<string> GetPublicKeyAsync(CancellationToken ct = default)
    {
        return (await File.ReadAllTextAsync(PublicKeyPath, ct)).Trim();
    }
}
