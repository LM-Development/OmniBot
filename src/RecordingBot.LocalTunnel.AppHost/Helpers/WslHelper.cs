using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RecordingBot.LocalTunnel.AppHost.Helpers;

/// <summary>
/// Executes commands inside WSL from Windows.
/// </summary>
public static class WslHelper
{
    /// <summary>
    /// Converts a Windows path to a WSL-compatible path.
    /// E.g. C:\Users\me\.aks-tunnel\id_ed25519  →  /mnt/c/Users/me/.aks-tunnel/id_ed25519
    /// </summary>
    public static string ToWslPath(string windowsPath)
    {
        var full = Path.GetFullPath(windowsPath);

        // Drive letter
        var drive = char.ToLowerInvariant(full[0]);
        var rest = full[2..].Replace('\\', '/'); // skip "C:" 

        return $"/mnt/{drive}{rest}";
    }

    /// <summary>
    /// Runs a command inside WSL and returns stdout. Throws on non-zero exit.
    /// </summary>
    public static async Task<string> RunAsync(string command, CancellationToken ct = default)
    {
        Console.WriteLine($"  [wsl] {command}");

        var psi = new ProcessStartInfo
        {
            FileName = "wsl",
            Arguments = $"bash -l -c \"{command.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start WSL process.");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"WSL command failed (exit {proc.ExitCode}):\n  cmd: {command}\n  stderr: {stderr}");
        }

        return stdout.ToString().Trim();
    }
}
