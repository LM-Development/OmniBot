using System;
using System.Threading.Tasks;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using RecordingBot.LocalTunnel.AppHost;
using RecordingBot.LocalTunnel.AppHost.Helpers;

// ──────────────────────────────────────────────────────────────────
// Local Development Tunnel – Aspire AppHost
//
// Orchestrates local development against a remote AKS cluster, matching
// the deployed production architecture where:
//   - Ingress controller terminates TLS for HTTPS signaling traffic
//   - Bot handles HTTP for signaling, TLS for media
//
// Setup phases:
//   1. SSH key generation
//   2. Helm chart deployment (tunnel pod + ingress in AKS)
//   3. TLS certificate extraction from AKS cert-manager secret
//   4. kubectl port-forward → SSH reverse tunnel → local bot
//
// Traffic flow:
//   Teams → Ingress (HTTPS:8443) → Tunnel Pod (HTTP:9441)
//        → SSH Tunnel → Local Bot (HTTP:9442)
//   Teams → LoadBalancer (TCP:28551) → Tunnel Pod (TCP:8445)
//        → SSH Tunnel → Local Bot (TLS:8445)
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
    // ── Phase 1: Deploy tunnel chart ─────────────────────────────────
    Console.WriteLine("[1/4] Helm Chart Deployment");
    Console.WriteLine("  Deploying tunnel pod with ingress (passwordless SSH)...");
    await HelmHelper.DeployAsync(config, string.Empty);
    Console.WriteLine("  ✓ Tunnel pod deployed");
    Console.WriteLine();

    // ── Phase 2: Extract certificate to local PFX ───────────────────
    Console.WriteLine("[2/4] Certificate Extraction");
    Console.WriteLine("  Extracting TLS certificate for media port...");
    certPath = await CertificateHelper.ExtractAsync(config);
    Console.WriteLine();

    // ── Phase 3: Wait for LoadBalancer IP ────────────────────────────
    Console.WriteLine("[3/4] LoadBalancer");
    externalIp = await KubernetesHelper.WaitForLoadBalancerIpAsync(config);
    Console.WriteLine();

    Console.WriteLine("╔══════════════════════════════════════════════╗");
    Console.WriteLine("║   Setup complete – starting services         ║");
    Console.WriteLine("╚══════════════════════════════════════════════╝");
    Console.WriteLine($"  Architecture:");
    Console.WriteLine($"    - Ingress handles TLS for HTTPS signaling");
    Console.WriteLine($"    - Bot handles TLS for media port");
    Console.WriteLine($"  External IP     : {externalIp}");
    Console.WriteLine($"  Host (HTTPS)    : {config.Host}");
    Console.WriteLine($"  HTTPS Port      : {config.PublicHttpsPort}");
    Console.WriteLine($"  Developer Path  : {config.DeveloperPathPrefix}");
    Console.WriteLine($"  Full URL        : https://{config.Host}:{(config.PublicHttpsPort == 443 ? "" : config.PublicHttpsPort)}{config.DeveloperPathPrefix}");
    Console.WriteLine($"  Media Port      : {config.PublicMediaPort}");
    Console.WriteLine($"  Certificate     : {certPath}");
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
//
// Architecture (matching deployed environment):
//   1. Ingress Controller: Terminates TLS for HTTPS traffic
//      - External: HTTPS on port 8443 (config.PublicHttpsPort)
//      - Internal: Routes to tunnel pod HTTP on port 9441 (config.SignalingPort)
//
//   2. SSH Tunnel: Forwards HTTP signaling + TCP media from tunnel pod to localhost
//      - Signaling: tunnel pod port 9441 → localhost:9442 (HTTP)
//      - Media: tunnel pod port 8445 → localhost:8445 (TCP)
//
//   3. Local Bot: Listens on HTTP for signaling, uses TLS for media
//      - HTTP signaling on port 9442 (CallSignalingPort + 1)
//      - Media with TLS using extracted certificate
//
// ─────────────────────────────────────────────────────────────────

// kubectl port-forward: maps local port to the tunnel pod's SSH ClusterIP service
var kubectlCommand = $"export PATH=\"$PATH:/snap/bin:/usr/local/bin\" && " +
    $"echo 'Checking if service {config.ReleaseName}-ssh exists in namespace {config.Namespace}...' && " +
    $"if ! kubectl --context {config.KubeContext} get svc/{config.ReleaseName}-ssh -n {config.Namespace} &>/dev/null; then " +
    $"echo 'ERROR: Service {config.ReleaseName}-ssh not found in namespace {config.Namespace}'; " +
    $"echo 'Make sure the Helm chart deployed successfully.'; " +
    $"kubectl --context {config.KubeContext} get svc -n {config.Namespace}; " +
    $"exit 1; fi && " +
    $"echo 'Service found! Starting kubectl port-forward to {config.ReleaseName}-ssh...' && " +
    $"kubectl --context {config.KubeContext} port-forward svc/{config.ReleaseName}-ssh -n {config.Namespace} {config.LocalSshPort}:22";

builder.AddExecutable("kubectl-forward", "wsl", ".",
    "bash", "-l", "-c", kubectlCommand);

// SSH reverse tunnel: retries until kubectl port-forward is ready, then keeps the tunnel alive.
// Note: Bot HTTP listener is on SignalingPort + 1, so we forward SignalingPort to SignalingPort + 1
// Authentication is handled by kubectl port-forward, so we use passwordless SSH
var sshCommand =
    $"echo 'Waiting for kubectl port-forward to establish...' && sleep 5 && " +
    $"while ! nc -z localhost {config.LocalSshPort} 2>/dev/null; do " +
    $"echo 'Port {config.LocalSshPort} not ready, waiting for kubectl port-forward...'; sleep 3; done && " +
    $"echo 'Port {config.LocalSshPort} is ready! Establishing SSH tunnel...' && " +
    $"echo 'Note: Using passwordless SSH (secured by kubectl port-forward)' && " +
    $"while true; do " +
    $"echo | ssh -tt " +
    $"-o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o ExitOnForwardFailure=yes " +
    $"-o ServerAliveInterval=15 -o ServerAliveCountMax=3 " +
    $"-R 0.0.0.0:{config.SignalingPort}:localhost:{config.SignalingPort + 1} " +
    $"-R 0.0.0.0:{config.MediaPort}:localhost:{config.MediaPort} " +
    $"-p {config.LocalSshPort} tunnel@localhost -N 2>&1 | tee /tmp/ssh-debug.log; " +
    $"SSH_EXIT=$?; " +
    $"if [ $SSH_EXIT -eq 0 ] || grep -q 'Authenticated to' /tmp/ssh-debug.log; then " +
    $"echo 'SSH tunnel established successfully!'; break; " +
    $"elif grep -q 'Connection reset' /tmp/ssh-debug.log; then " +
    $"echo 'Connection reset, retrying in 3s...'; sleep 3; " +
    $"else " +
    $"echo \"SSH tunnel failed (exit $SSH_EXIT), retrying in 3s...\"; " +
    $"tail -10 /tmp/ssh-debug.log; sleep 3; " +
    $"fi; done";

builder.AddExecutable("ssh-tunnel", "wsl", ".",
    "bash", "-c", sshCommand);

// The Teams recording bot, configured for local development
builder.AddProject<Projects.RecordingBot_Console>("recording-bot")
    // Authentication settings - TODO: Set these in user secrets or environment variables
    .WithEnvironment("AzureSettings__AadAppId", builder.Configuration["AzureSettings:AadAppId"] ?? "")
    .WithEnvironment("AzureSettings__AadAppSecret", builder.Configuration["AzureSettings:AadAppSecret"] ?? "")

    // Certificate configuration
    .WithEnvironment("AzureSettings__CertificatePath", certPath)
    .WithEnvironment("AzureSettings__CertificatePassword", "")

    // Network configuration
    .WithEnvironment("AzureSettings__ServiceDnsName", config.Host)
    .WithEnvironment("AzureSettings__ServicePath", config.DeveloperPathPrefix)
    .WithEnvironment("AzureSettings__ServiceCname", config.Host)
    .WithEnvironment("AzureSettings__CallSignalingPort", config.SignalingPort.ToString())
    .WithEnvironment("AzureSettings__CallSignalingPublicPort", config.PublicHttpsPort.ToString())
    .WithEnvironment("AzureSettings__InstanceInternalPort", config.MediaPort.ToString())
    .WithEnvironment("AzureSettings__InstancePublicPort", config.PublicMediaPort.ToString())

    // Graph API endpoint
    .WithEnvironment("AzureSettings__PlaceCallEndpointUrl", "https://graph.microsoft.com/v1.0")

    // Pod identification
    .WithEnvironment("AzureSettings__PodName", "local")

    // Media recording configuration
    .WithEnvironment("AzureSettings__MediaFolder", builder.Configuration["AzureSettings:MediaFolder"] ?? "archive")
    .WithEnvironment("AzureSettings__IsStereo", builder.Configuration["AzureSettings:IsStereo"] ?? "false")
    .WithEnvironment("AzureSettings__WAVSampleRate", builder.Configuration["AzureSettings:WAVSampleRate"] ?? "0")
    .WithEnvironment("AzureSettings__WAVQuality", builder.Configuration["AzureSettings:WAVQuality"] ?? "100")

    // Event capture configuration
    .WithEnvironment("AzureSettings__CaptureEvents", builder.Configuration["AzureSettings:CaptureEvents"] ?? "false")
    .WithEnvironment("AzureSettings__EventsFolder", builder.Configuration["AzureSettings:EventsFolder"] ?? "events")
    .WithEnvironment("AzureSettings__TopicName", builder.Configuration["AzureSettings:TopicName"] ?? "recordingbotevents")
    .WithEnvironment("AzureSettings__TopicKey", builder.Configuration["AzureSettings:TopicKey"] ?? "")
    .WithEnvironment("AzureSettings__RegionName", builder.Configuration["AzureSettings:RegionName"] ?? "australiaeast");

await builder.Build().RunAsync();

static string GetKeyFingerprint(string publicKey)
{
    var parts = publicKey.Split(' ');
    if (parts.Length >= 2)
    {
        return $"{parts[0]} ...{parts[1][^8..]}";
    }
    return publicKey.Length > 50 ? $"{publicKey[..50]}..." : publicKey;
}

static async Task VerifyPodDeploymentAsync(TunnelConfiguration config, string localPublicKey)
{
    try
    {
        // Check if pod is running
        var podName = await WslHelper.RunAsync(
            $"kubectl get pod -n {config.Namespace} -l app={config.ReleaseName} " +
            $"--context {config.KubeContext} -o jsonpath='{{.items[0].metadata.name}}'");

        if (string.IsNullOrWhiteSpace(podName))
        {
            Console.WriteLine("  WARNING: Could not find tunnel pod");
            return;
        }

        Console.WriteLine($"  Pod: {podName}");

        // Wait a moment for the pod to fully initialize
        await Task.Delay(2000);

        // Get the authorized_keys content from the pod
        var podKey = await WslHelper.RunAsync(
            $"kubectl exec -n {config.Namespace} {podName} " +
            $"--context {config.KubeContext} -- " +
            $"cat /etc/ssh/authorized_keys/tunnel 2>/dev/null || echo 'NOTFOUND'");

        if (podKey.Contains("NOTFOUND") || string.IsNullOrWhiteSpace(podKey))
        {
            Console.WriteLine("  ERROR: authorized_keys file not found in pod!");
            Console.WriteLine("  The pod may still be starting. Try restarting the Aspire host.");
            return;
        }

        Console.WriteLine("  ✓ SSH authorized_keys file exists");

        // Compare keys
        var localKeyTrimmed = localPublicKey.Trim();
        var podKeyTrimmed = podKey.Trim();

        if (localKeyTrimmed == podKeyTrimmed)
        {
            Console.WriteLine("  ✓ SSH key matches! Authentication should work.");
        }
        else
        {
            Console.WriteLine("  ✗ ERROR: SSH key MISMATCH!");
            Console.WriteLine($"  Local key: {GetKeyFingerprint(localKeyTrimmed)}");
            Console.WriteLine($"  Pod key:   {GetKeyFingerprint(podKeyTrimmed)}");
            Console.WriteLine();
            Console.WriteLine("  SOLUTION: Delete the local SSH keys and redeploy:");
            Console.WriteLine($"    Remove-Item -Recurse -Force $env:USERPROFILE\\.aks-tunnel");
            Console.WriteLine("    Then restart the Aspire host");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  WARNING: Could not verify pod: {ex.Message}");
    }
}
