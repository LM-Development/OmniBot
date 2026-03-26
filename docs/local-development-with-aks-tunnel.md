# Local Development with AKS Reverse Tunnel

Debug your Teams recording bot locally while it's reachable from the internet, using your existing AKS cluster as a publicly-accessible tunnel endpoint. No third-party tunneling services (ngrok, Dev Tunnels) required.

## Architecture

```
Internet  →  AKS LoadBalancer :8443/:28551  (TCP passthrough)
                     ↓
              Tunnel Pod  (SSH server, NO TLS)
                     ↓
              kubectl port-forward  (WSL → AKS pod :22)
                     ↓
              SSH reverse tunnel  (-R 0.0.0.0:9441:localhost:9441)
                     ↓
              WSL (mirrored mode)  →  Windows localhost
                     ↓
              Local Bot Application  (handles TLS with extracted cert)
```

**Key points:**

- The tunnel pod is a lightweight Alpine container with an SSH server — it does **not** handle TLS.
- TLS termination happens on your local Windows machine, exactly as it does in the production container.
- The certificate is extracted from the existing cert-manager Kubernetes secret and imported into your local Windows certificate store.
- WSL mirrored networking mode means the bot on Windows can bind to the same `localhost` ports that the SSH reverse tunnel forwards to.

## Prerequisites

- **WSL 2** with mirrored networking mode enabled (`.wslconfig`):
  ```ini
  [wsl2]
  networkingMode=mirrored
  ```
- **kubectl** installed in WSL, configured with your AKS context
- **helm v3** installed in WSL
- **ssh** and **ssh-keygen** available in WSL (usually pre-installed)
- **VC++ Redistributable** installed on Windows (required by Microsoft.Skype.Bots.Media)
- **.NET 8 SDK** on Windows

## Quick Start (Aspire AppHost)

The Aspire AppHost automates the entire setup: SSH keys, Helm deployment, certificate extraction, tunnel establishment, and bot startup.

### 1. Configure

Edit `src/RecordingBot.LocalTunnel.AppHost/appsettings.json`:

```json
{
  "Tunnel": {
    "KubeContext": "my-aks-cluster",
    "Namespace": "dev-tunnel",
    "ReleaseName": "local-dev-tunnel",
    "BotReleaseName": "teams-recording-bot",
    "BotNamespace": "default",
    "Host": "bot.example.com",
    "PublicHttpsPort": 8443,
    "PublicMediaPort": 28551,
    "SignalingPort": 9441,
    "MediaPort": 8445,
    "LocalSshPort": 2222,
    "PublicIp": null,
    "ChartPath": "../../deploy/local-dev-tunnel"
  }
}
```

Key values to fill in:

| Setting | Description |
|---------|-------------|
| `KubeContext` | Your AKS kubectl context name (run `kubectl config get-contexts` in WSL) |
| `Host` | The FQDN of your bot (must match the cert-manager certificate) |
| `BotReleaseName` | The Helm release name of your deployed bot (used to find the TLS secret) |
| `BotNamespace` | Namespace where the bot's TLS secret lives |
| `PublicIp` | (Optional) Pin the LoadBalancer to a specific IP |

You also need to configure the bot secrets (AAD app, etc.) via a `.env` file in `src/RecordingBot.Console/`. The AppHost will set the tunnel-specific env vars automatically.

### 2. Run

```powershell
cd src
dotnet run --project RecordingBot.LocalTunnel.AppHost
```

The AppHost will:

1. **Generate SSH keys** in `~/.aks-tunnel/` (first run only)
2. **Deploy the tunnel Helm chart** to your AKS cluster
3. **Extract the TLS certificate** from the cert-manager secret
4. **Import it** into your Windows certificate store
5. **Start kubectl port-forward** and the **SSH reverse tunnel**
6. **Launch the bot** with correct configuration

The Aspire dashboard (http://localhost:15888) shows all running resources.

### 3. Debug

Set `RecordingBot.LocalTunnel.AppHost` as the startup project in Visual Studio and press **F5**. The bot process will be launched with the debugger attached.

## Manual Setup (Shell Scripts)

If you prefer to manage the tunnel manually without Aspire.

### Deploy Tunnel & Start

From WSL:

```bash
# Deploy tunnel and start port forwarding + SSH tunnel
./scripts/local-tunnel/setup-tunnel.sh <kube-context> <namespace>

# Example:
./scripts/local-tunnel/setup-tunnel.sh my-aks default
```

### Extract Certificate

From WSL:

```bash
./scripts/local-tunnel/extract-cert.sh <kube-context> <namespace> <tls-secret-name> ./certs

# Example:
./scripts/local-tunnel/extract-cert.sh my-aks default ingress-tls-teams-recording-bot ./certs
```

Set the certificate path in your `.env` file:

```
AzureSettings__CertificatePath=./certs/tunnel-cert.pfx
```

### Run the Bot

```powershell
cd src\RecordingBot.Console
dotnet run
```

## Cleanup

### Uninstall the tunnel from AKS

```bash
helm uninstall local-dev-tunnel --kube-context <context> --namespace dev-tunnel
```

### Remove local SSH keys and certificates

```powershell
Remove-Item -Recurse -Force "$env:USERPROFILE\.aks-tunnel"
```

### Remove certificate files

The extracted PFX and PEM files are stored in `~/.aks-tunnel/certs/` and are removed by the command above.

## Helm Chart Details

The `deploy/local-dev-tunnel/` chart deploys:

| Resource | Purpose |
|----------|---------|
| **Deployment** | Alpine pod with OpenSSH server, configured with `GatewayPorts yes` for reverse tunneling |
| **Service (LoadBalancer)** | Exposes HTTPS (:8443→:9441) and media (:28551→:8445) publicly |
| **Service (ClusterIP)** | Internal SSH service (:22), reachable only via `kubectl port-forward` |
| **ConfigMap** | SSH server configuration |

The chart uses SSH public key authentication. The public key is passed as a Helm value and written to the pod's authorized_keys file.

### Customizable Values

| Value | Default | Description |
|-------|---------|-------------|
| `ssh.authorizedKey` | `""` | SSH public key (set automatically by AppHost) |
| `ports.signaling` | `9441` | Pod port for call signaling |
| `ports.media` | `8445` | Pod port for media |
| `service.httpsPort` | `8443` | Public HTTPS port on LoadBalancer |
| `service.mediaPort` | `28551` | Public media port on LoadBalancer |
| `service.publicIp` | `null` | Pin LoadBalancer to specific IP |

## Troubleshooting

### SSH tunnel fails to connect

- Ensure kubectl port-forward is running: `kubectl get pods -n dev-tunnel`
- Check the pod logs: `kubectl logs -n dev-tunnel -l app=local-dev-tunnel`
- Verify the SSH key was deployed: `kubectl exec -n dev-tunnel deploy/local-dev-tunnel -- cat /etc/ssh/authorized_keys/tunnel`

### Certificate loading fails

- Verify the PFX file exists at the path shown during setup (default: `~/.aks-tunnel/certs/tunnel-cert.pfx`).
- The bot loads the certificate directly from the PFX file when `AzureSettings:CertificatePath` is set. No Windows certificate store import is needed.
- For production / container deployments the bot falls back to store lookup via `AzureSettings:CertificateThumbprint`.

### Bot starts but Teams can't reach it

- Verify the LoadBalancer has an external IP: `kubectl get svc -n dev-tunnel`
- Check DNS: the `Host` must resolve to the LoadBalancer IP (or use the IP directly in the Bot Framework registration).
- Verify the SSH reverse tunnel is active (check the Aspire dashboard or process list).
- Test connectivity: `curl -k https://<external-ip>:8443/` should reach your local bot.

### WSL networking issues

- Confirm mirrored mode: `wsl --version` should show networkingMode=mirrored.
- Test that a port opened in WSL is reachable from Windows: `nc -l 9441` in WSL, `Test-NetConnection localhost -Port 9441` in PowerShell.
