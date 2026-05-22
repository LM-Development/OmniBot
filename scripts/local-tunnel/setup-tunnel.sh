#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────
# setup-tunnel.sh
#
# Deploys the local-dev-tunnel Helm chart and establishes a reverse
# SSH tunnel from the AKS pod to localhost. Run this from WSL.
#
# Prerequisites:
#   - kubectl configured with your AKS context
#   - helm v3 installed
#   - ssh-keygen, ssh, sshpass (or SSH key pair)
#
# Usage:
#   ./setup-tunnel.sh <kube-context> <namespace> [release-name] [public-https-port] [public-media-port]
#
# The script will:
#   1. Generate an SSH key pair (if not present)
#   2. Deploy the Helm chart with the public key
#   3. Wait for the LoadBalancer IP
#   4. Start kubectl port-forward
#   5. Start the SSH reverse tunnel
# ──────────────────────────────────────────────────────────────────
set -euo pipefail

CONTEXT="${1:?Usage: $0 <kube-context> <namespace> [release-name]}"
NAMESPACE="${2:?Usage: $0 <kube-context> <namespace> [release-name]}"
RELEASE="${3:-local-dev-tunnel}"
CHART_DIR="$(cd "$(dirname "$0")/../../deploy/local-dev-tunnel" && pwd)"
KEY_DIR="$HOME/.aks-tunnel"
KEY_PATH="$KEY_DIR/id_ed25519"

SIGNALING_PORT=9441
MEDIA_PORT=8445
LOCAL_SSH_PORT=2222
PUBLIC_HTTPS_PORT=${4:-8443}
PUBLIC_MEDIA_PORT=${5:-28551}

cleanup() {
  echo ""
  echo "Shutting down tunnel..."
  [ -n "${SSH_PID:-}" ] && kill "$SSH_PID" 2>/dev/null || true
  [ -n "${KUBECTL_PID:-}" ] && kill "$KUBECTL_PID" 2>/dev/null || true
  echo "Done."
}
trap cleanup EXIT INT TERM

# ── 1. SSH Key ────────────────────────────────────────────────────
mkdir -p "$KEY_DIR"
if [ ! -f "$KEY_PATH" ]; then
  echo "[1/5] Generating SSH key pair..."
  ssh-keygen -t ed25519 -f "$KEY_PATH" -N "" -q
else
  echo "[1/5] SSH key pair exists."
fi
PUBLIC_KEY=$(cat "${KEY_PATH}.pub")

# ── 2. Deploy Helm chart ─────────────────────────────────────────
echo "[2/5] Deploying Helm chart..."
helm upgrade "$RELEASE" "$CHART_DIR" \
  --kube-context "$CONTEXT" \
  --namespace "$NAMESPACE" --create-namespace \
  --install --wait --timeout 120s \
  --set ssh.authorizedKey="$PUBLIC_KEY" \
  --set ports.signaling="$SIGNALING_PORT" \
  --set ports.media="$MEDIA_PORT"

# ── 3. Wait for LoadBalancer IP ───────────────────────────────────
echo "[3/5] Waiting for LoadBalancer IP..."
for i in $(seq 1 60); do
  IP=$(kubectl get svc "$RELEASE" \
    --context "$CONTEXT" -n "$NAMESPACE" \
    -o jsonpath='{.status.loadBalancer.ingress[0].ip}' 2>/dev/null || true)
  if [ -n "$IP" ] && [ "$IP" != "<pending>" ]; then
    echo "  External IP: $IP"
    break
  fi
  sleep 3
done
if [ -z "${IP:-}" ]; then
  echo "ERROR: Timed out waiting for LoadBalancer IP."
  exit 1
fi

# ── 4. kubectl port-forward ───────────────────────────────────────
echo "[4/5] Starting kubectl port-forward..."
kubectl --context "$CONTEXT" port-forward "svc/${RELEASE}-ssh" -n "$NAMESPACE" "${LOCAL_SSH_PORT}:22" &
KUBECTL_PID=$!
sleep 3

# ── 5. SSH reverse tunnel ────────────────────────────────────────
echo "[5/5] Establishing SSH reverse tunnel..."
echo "  -R 0.0.0.0:${SIGNALING_PORT}:localhost:${SIGNALING_PORT}"
echo "  -R 0.0.0.0:${MEDIA_PORT}:localhost:${MEDIA_PORT}"
echo ""
echo "Tunnel is active. Traffic on ${IP}:${PUBLIC_HTTPS_PORT} → localhost:${SIGNALING_PORT}"
echo "                          ${IP}:${PUBLIC_MEDIA_PORT} → localhost:${MEDIA_PORT}"
echo "Press Ctrl+C to stop."
echo ""

ssh -i "$KEY_PATH" \
  -o StrictHostKeyChecking=no \
  -o UserKnownHostsFile=/dev/null \
  -o ExitOnForwardFailure=yes \
  -o ServerAliveInterval=15 \
  -o ServerAliveCountMax=3 \
  -R "0.0.0.0:${SIGNALING_PORT}:localhost:${SIGNALING_PORT}" \
  -R "0.0.0.0:${MEDIA_PORT}:localhost:${MEDIA_PORT}" \
  -p "$LOCAL_SSH_PORT" tunnel@localhost -N &
SSH_PID=$!

wait "$SSH_PID"
