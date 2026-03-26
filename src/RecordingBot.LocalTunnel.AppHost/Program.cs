using System;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using RecordingBot.LocalTunnel.AppHost;
using RecordingBot.LocalTunnel.AppHost.Helpers;

// ──────────────────────────────────────────────────────────────────
// Local Development Tunnel – Aspire AppHost
//
// Orchestrates:
//   1. SSH key generation
//   2. Helm chart deployment (reverse-tunnel pod in AKS)
//   3. TLS certificate extraction from the AKS cert-manager secret
//   4. kubectl port-forward → SSH reverse tunnel → local bot
// ──────────────────────────────────────────────────────────────────

var builder = DistributedApplication.CreateBuilder(args);

// ── Configuration ────────────────────────────────────────────────
var config = builder.Configuration.GetSection("Tunnel").Get<TunnelConfiguration>()
    ?? throw new InvalidOperationException(
        "Missing 'Tunnel' section in appsettings.json. " +
        "Copy appsettings.json and fill in your AKS details.");

config.Validate();


Console.WriteLine("╔══════════════════════════════════════════════╗");
Console.WriteLine("║   Local Development Tunnel – Setup Phase     ║");
Console.WriteLine("╚══════════════════════════════════════════════╝");
Console.WriteLine();

string certPath;
string externalIp;

try
{
    // ── Phase 1: SSH key pair ────────────────────────────────────────
    Console.WriteLine("[1/4] SSH Key Pair");
    await SshKeyHelper.EnsureKeyPairAsync();
    var publicKey = await SshKeyHelper.GetPublicKeyAsync();
    Console.WriteLine();

    // ── Phase 2: Deploy tunnel chart ─────────────────────────────────
    Console.WriteLine("[2/4] Helm Chart Deployment");
    await HelmHelper.DeployAsync(config, publicKey);
    Console.WriteLine();

    // ── Phase 3: Extract certificate to local PFX ───────────────────
    Console.WriteLine("[3/4] Certificate Extraction");
    certPath = await CertificateHelper.ExtractAsync(config);
    Console.WriteLine();

    // ── Phase 4: Wait for LoadBalancer IP ────────────────────────────
    Console.WriteLine("[4/4] LoadBalancer");
    externalIp = await KubernetesHelper.WaitForLoadBalancerIpAsync(config);
    Console.WriteLine();

    Console.WriteLine("╔══════════════════════════════════════════════╗");
    Console.WriteLine("║   Setup complete – starting services         ║");
    Console.WriteLine("╚══════════════════════════════════════════════╝");
    Console.WriteLine($"  External IP : {externalIp}");
    Console.WriteLine($"  Host        : {config.Host}");
    Console.WriteLine($"  Certificate : {certPath}");
    Console.WriteLine();
}
catch (Exception ex)
{
    Console.WriteLine();
    Console.WriteLine("╔══════════════════════════════════════════════╗");
    Console.WriteLine("║   Setup FAILED                               ║");
    Console.WriteLine("╚══════════════════════════════════════════════╝");
    Console.WriteLine($"Error: {ex.GetType().Name}");
    Console.WriteLine($"Message: {ex.Message}");
    if (ex.InnerException != null)
    {
        Console.WriteLine($"Inner: {ex.InnerException.Message}");
    }
    Console.WriteLine();
    Console.WriteLine("Stack trace:");
    Console.WriteLine(ex.StackTrace);
    Console.WriteLine();
    Console.WriteLine("Press any key to exit...");
    Console.ReadKey();
    return;
}

// ── Aspire Resources ─────────────────────────────────────────────

var wslKeyPath = WslHelper.ToWslPath(SshKeyHelper.PrivateKeyPath);

// kubectl port-forward: maps local port to the tunnel pod's SSH ClusterIP service
builder.AddExecutable("kubectl-forward", "wsl", ".",
    "kubectl", "--context", config.KubeContext,
    "port-forward", $"svc/{config.ReleaseName}-ssh",
    "-n", config.Namespace,
    $"{config.LocalSshPort}:22");

// SSH reverse tunnel: retries until kubectl port-forward is ready,
// then keeps the tunnel alive.
var sshCommand =
    $"while ! ssh -i {wslKeyPath} " +
    $"-o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o ExitOnForwardFailure=yes " +
    $"-o ServerAliveInterval=15 -o ServerAliveCountMax=3 " +
    $"-R 0.0.0.0:{config.SignalingPort}:localhost:{config.SignalingPort} " +
    $"-R 0.0.0.0:{config.MediaPort}:localhost:{config.MediaPort} " +
    $"-p {config.LocalSshPort} tunnel@localhost -N 2>/dev/null; do " +
    $"echo 'SSH tunnel not ready, retrying in 3s...'; sleep 3; done";

builder.AddExecutable("ssh-tunnel", "wsl", ".",
    "bash", "-c", sshCommand);

// The Teams recording bot, configured for local development
builder.AddProject<Projects.RecordingBot_Console>("recording-bot")
    .WithEnvironment("AzureSettings__CertificatePath", certPath)
    .WithEnvironment("AzureSettings__ServiceDnsName", config.Host)
    .WithEnvironment("AzureSettings__CallSignalingPort", config.SignalingPort.ToString())
    .WithEnvironment("AzureSettings__CallSignalingPublicPort", config.PublicHttpsPort.ToString())
    .WithEnvironment("AzureSettings__InstanceInternalPort", config.MediaPort.ToString())
    .WithEnvironment("AzureSettings__InstancePublicPort", config.PublicMediaPort.ToString())
    .WithEnvironment("AzureSettings__PodName", "bot-0");

await builder.Build().RunAsync();
