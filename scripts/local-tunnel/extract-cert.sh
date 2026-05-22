#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────
# extract-cert.sh
#
# Extracts the TLS certificate from the AKS cert-manager secret and
# saves PEM files locally. For use outside of the Aspire AppHost.
#
# Usage:
#   ./extract-cert.sh <kube-context> <namespace> <secret-name> [output-dir]
#
# Example:
#   ./extract-cert.sh my-aks-ctx default ingress-tls-teams-recording-bot ./certs
# ──────────────────────────────────────────────────────────────────
set -euo pipefail

CONTEXT="${1:?Usage: $0 <kube-context> <namespace> <secret-name> [output-dir]}"
NAMESPACE="${2:?Usage: $0 <kube-context> <namespace> <secret-name> [output-dir]}"
SECRET_NAME="${3:?Usage: $0 <kube-context> <namespace> <secret-name> [output-dir]}"
OUTPUT_DIR="${4:-.}"

mkdir -p "$OUTPUT_DIR"

echo "Extracting TLS certificate from secret '$SECRET_NAME' in '$NAMESPACE' (context: $CONTEXT)..."

kubectl get secret "$SECRET_NAME" \
  --context "$CONTEXT" \
  -n "$NAMESPACE" \
  -o jsonpath='{.data.tls\.crt}' | base64 -d > "$OUTPUT_DIR/tls.crt"

kubectl get secret "$SECRET_NAME" \
  --context "$CONTEXT" \
  -n "$NAMESPACE" \
  -o jsonpath='{.data.tls\.key}' | base64 -d > "$OUTPUT_DIR/tls.key"

echo "Saved:"
echo "  $OUTPUT_DIR/tls.crt"
echo "  $OUTPUT_DIR/tls.key"

# Optionally convert to PFX (requires openssl)
if command -v openssl &> /dev/null; then
  openssl pkcs12 -export -out "$OUTPUT_DIR/tunnel-cert.pfx" \
    -passout pass: \
    -inkey "$OUTPUT_DIR/tls.key" \
    -in "$OUTPUT_DIR/tls.crt"
  echo "  $OUTPUT_DIR/tunnel-cert.pfx  (password: empty)"
fi

echo ""
echo "To import on Windows (run as admin in PowerShell):"
echo "  certutil -f -p \"\" -importpfx $OUTPUT_DIR\\tunnel-cert.pfx"
echo ""
echo "Then retrieve the thumbprint:"
echo "  (Get-PfxCertificate -FilePath $OUTPUT_DIR\\tunnel-cert.pfx).Thumbprint"
