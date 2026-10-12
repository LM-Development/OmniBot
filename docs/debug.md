
## Signaling and media hosts

The bot is reached on two endpoints:

- **Signaling** (HTTPS, port 443): Teams sends incoming calls and call notifications to
  `https://<signaling host>/<pod>/api/calling/notification`.
- **Media** (TCP/TLS, `AzureSettings__InstancePublicPort`): the media platform connects to
  `<media host>:<port>`. The certificate must match the media host.

On AKS both run on the same host. With ngrok they are different: an HTTP tunnel for signaling and a
reserved TCP address (with your own domain as CNAME) for media. Configure both in your `.env`:

```
AzureSettings__ServiceDnsName=media.contoso.com           ## CNAME -> N.tcp.ngrok.io, certificate CN
AzureSettings__ServiceSignalingDnsName=yourname.ngrok.app ## ngrok HTTP tunnel -> https://localhost:9441
AzureSettings__InstancePublicPort=RESERVED_PORT           ## port of the reserved ngrok TCP address
AzureSettings__InstanceInternalPort=8445                  ## local port the TCP tunnel forwards to
```

`AzureSettings__ServiceSignalingDnsName` falls back to the media host when it is not set. Without it,
Teams sends call notifications to the TCP tunnel host, they never reach the bot, and calls fail
without an error in the bot's log ([#64](https://github.com/LM-Development/OmniBot/issues/64)).

To check that notifications arrive, watch the ngrok inspector (`http://localhost:4040`) while a call
is set up, or while muting and unmuting during a call.

## A list of common issues when debugging locally

### Local Console app

You are running the bot within visual studio (as a console app) and it starts, runs and closes unexpectedly early.

**Possible Solution:**

Run Visual Studio with Administrative privileges (run in Admin mode - indicator in top right of Visual Studio will confirm)

### Call Status

If your Bot Call status is always Establishing and its never Established

The messaginb bot is unable to commmunicate with the media server

**Possible solutions:**

1. Check SSL Certificates
2. Rerun certs.bat
3. Ngrok check that running and is redirecting correctly (no errors or missing responses)
4. Ngrok check local firewall (incoming and outgoing)
5. Check Reserved TCP Address in Ngrok site is setup

(To be confirmed: there has been some mentions of only 0 and 1 subdomains in Ngrok being useful e.g.
1.tcp.ngrok.io:27188)

### Cannot add participant

If your media service cannot connect to your local media processor and returns a *MediaControllerConversationAddParticipantFailed*

**Possible solution:**

1. Confirm that Ngrok is running locally
2. Confirm that your ports match the port Ngrok opened up for you (.env file) - `AzureSettings__InstancePublicPort=RESERVED_PORT`
3. your `CName` points to the right instance of the Ngrok tcp proxy.

### Calls fail without errors in the bot

The bot answers, but the call drops shortly after and nothing is logged.

**Possible solution:**

Call notifications are not reaching the bot. Set `AzureSettings__ServiceSignalingDnsName` to your
ngrok HTTP domain, see [Signaling and media hosts](#signaling-and-media-hosts).

### Other things to try

Another common issue is not using the right certificate for your bot.
Confirm the certificate is in the correct place, has not expired, the thumbprint is correct and the the certs.bat rules have been run.
