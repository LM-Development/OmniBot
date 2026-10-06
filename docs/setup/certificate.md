# Setting up the TLS Certificate

The bot needs a TLS certificate when it runs outside of Kubernetes (local debugging, a VM, a bare Windows host).
On AKS you can skip this page: `cert-manager` issues and renews the certificate for you, see [Deploy with Helm](../tutorials/deploy/5-helm.md) and [Deploying with a Custom Domain](../guides/deploy-with-custom-domain.md).

## Why a certificate is needed

The Microsoft media platform opens a TLS connection to the bot on `AzureSettings__ServiceDnsName`:`AzureSettings__InstancePublicPort`.
For that connection to succeed you need:

1. **A machine the bot can run on and bind ports on**, reachable from the internet — either directly (public IP, e.g. an Azure VM) or through a TCP tunnel (e.g. an ngrok reserved TCP address, see [Setting up Ngrok](ngrok.md)).
2. **A domain you control** that resolves to that machine or tunnel. This is the value of `AzureSettings__ServiceDnsName`.
   - Public IP: an `A` record, or the `*.cloudapp.azure.com` DNS label of an Azure public IP.
   - ngrok: a `CNAME` record pointing to your reserved TCP address host (e.g. `media.contoso.com` → `1.tcp.ngrok.io`).
3. **A certificate for exactly that domain**, issued by a publicly trusted CA (e.g. Let's Encrypt), installed in the Windows `LocalMachine\My` store.
   The bot loads it by thumbprint (`AzureSettings__CertificateThumbprint`) and uses it for the call signaling endpoint and for media.

> [!IMPORTANT]
> The certificate must match the domain in `AzureSettings__ServiceDnsName`. A certificate for your ngrok HTTP domain (`*.ngrok.io` / `*.ngrok-free.app`) does **not** work for media, because the TCP tunnel is reached through your own domain.

## Prerequisites

- A domain where you can create DNS records (see above)
- [Certbot](https://certbot.eff.org/instructions) — on Windows, running certbot inside WSL or a Linux container is the easiest option
- [OpenSSL](openssl.md) (already available in WSL / most Linux distributions)

## 1. Request the certificate (DNS challenge)

Use the **DNS-01 challenge**. It only requires a TXT record in your DNS zone and works regardless of how the bot is reached.

> [!NOTE]
> The HTTP-01 challenge (certbot `--standalone`) through an ngrok tunnel is known to fail with a `404` from `acme.ngrok.com` ([#26](https://github.com/LM-Development/OmniBot/issues/26)). Use DNS-01 instead.

```bash
sudo certbot certonly --manual --preferred-challenges dns -d media.contoso.com
```

Certbot asks you to create a TXT record named `_acme-challenge.media.contoso.com`. Create it at your DNS provider, wait until it resolves (`nslookup -type=TXT _acme-challenge.media.contoso.com`), then press Enter.

On success, the files are written to `/etc/letsencrypt/live/media.contoso.com/`.

> [!TIP]
> `--manual` certificates cannot renew automatically. If your DNS provider has a [certbot DNS plugin](https://eff-certbot.readthedocs.io/en/stable/using.html#dns-plugins) (Azure DNS, Cloudflare, Route 53, ...), use it instead so `certbot renew` works unattended.

## 2. Export a PFX

Combine the private key and the **full chain** into a PFX file. Including the intermediate certificates is required — otherwise the media platform cannot build the chain.

```bash
sudo openssl pkcs12 -export \
    -out certificate.pfx \
    -inkey /etc/letsencrypt/live/media.contoso.com/privkey.pem \
    -in /etc/letsencrypt/live/media.contoso.com/fullchain.pem \
    -passout pass:YOUR_PFX_PASSWORD
```

Copy `certificate.pfx` to the Windows machine that runs the bot.

## 3. Install the certificate

Run in an **elevated** PowerShell on the bot machine:

```powershell
$password = Read-Host -AsSecureString -Prompt "PFX password"
Import-PfxCertificate -FilePath .\certificate.pfx -CertStoreLocation Cert:\LocalMachine\My -Password $password
```

The output shows the `Thumbprint`. You can also list it later:

```powershell
Get-ChildItem Cert:\LocalMachine\My | Where-Object Subject -like "*media.contoso.com*" | Format-List Subject, Thumbprint, NotAfter
```

<details>
<summary>Alternative: install via the MMC snap-in</summary>

1. Press `WIN+R`, run `mmc`.
2. `File` → `Add/Remove Snap-in...` → add `Certificates`, choose `Computer account` → `Local computer` → `Finish`.
3. Expand `Certificates (Local Computer)` → `Personal`, right-click `Certificates` → `All Tasks` → `Import...`.
4. Select `certificate.pfx` (set the file filter to `Personal Information Exchange`), enter the password and finish the wizard.
5. Double-click the certificate → `Details` → copy the `Thumbprint`.

</details>

## 4. Configure the bot

Set the values in `src/RecordingBot.Console/.env` (copy it from [.env-template](../../src/RecordingBot.Console/.env-template)):

```
AzureSettings__ServiceDnsName=media.contoso.com
AzureSettings__CertificateThumbprint=THUMBPRINT
```

If the thumbprint is wrong or the certificate is not in `LocalMachine\My`, the bot fails on startup with `No certificate with thumbprint ... was found in the machine store.`

## 5. URL ACL and certificate bindings

1. Copy [scripts/certs.bat-template](../../scripts/certs.bat-template) to `scripts/certs.bat`.
2. Replace both occurrences of `YOUR_CERT_THUMBPRINT` with your thumbprint.
3. Run `certs.bat` from an **elevated** command prompt.

The script reads the ports from your `.env` and (re)creates the URL ACLs and `netsh http` certificate bindings for the call signaling and media ports.

## Renewing

Let's Encrypt certificates expire after 90 days. To renew:

1. Run `certbot renew` (or repeat step 1 for `--manual` certificates).
2. Repeat steps 2–5 — the new certificate has a **new thumbprint**, so update `.env` and `certs.bat`.

Let's Encrypt applies [rate limits](https://letsencrypt.org/docs/rate-limits/); keep your `/etc/letsencrypt` folder instead of requesting a new certificate every time.

## Troubleshooting

### Certbot fails with `unauthorized` / `404` on the HTTP challenge

Switch to the DNS challenge as described in [step 1](#1-request-the-certificate-dns-challenge).

### The bot starts but never joins a call

If the console only shows requests like `GET /` and no `POST /api/calling`, the platform is not reaching the bot's signaling endpoint. This is not a certificate problem. Check:

- The **Teams calling webhook** in the Azure Bot resource points to `https://<your signaling domain>/api/calling` (see [Bot registration](bot.md)).
- Your tunnel / port forwarding for the signaling port is running.
- The compliance recording policy is assigned to the calling user (see [Policy](../guides/policy.md)); policy assignments can take a while to apply.

### The bot receives the call, but it terminates while establishing

Check that:

- The certificate subject matches `AzureSettings__ServiceDnsName` exactly.
- The PFX contains the full chain (`fullchain.pem`, not `cert.pem`).
- The certificate has not expired, and the thumbprint in `.env` / `certs.bat` belongs to the current certificate.
- `AzureSettings__InstancePublicPort` is reachable from the internet and forwards to `AzureSettings__InstanceInternalPort`.
