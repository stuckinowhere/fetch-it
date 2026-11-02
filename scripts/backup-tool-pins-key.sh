#!/usr/bin/env bash
# Encrypt the tool-pins signing key into the local Google Drive folder.
# Run from Git Bash, or from PowerShell:
#   & "C:\Program Files\Git\bin\bash.exe" scripts/backup-tool-pins-key.sh
# Prints the passphrase ONCE: store it in your password manager.
set -euo pipefail

KEY="${1:-$HOME/.fetchit/tool-pins-signing.pem}"
DEST="${2:-/g/Mi unidad/fetch-it}"

[ -f "$KEY" ] || { echo "key not found: $KEY" >&2; exit 1; }
mkdir -p "$DEST"

PASS=$(openssl rand -base64 30 | tr -d '/+=' | cut -c1-32)
openssl enc -aes-256-cbc -pbkdf2 -iter 600000 -salt \
  -in "$KEY" -out "$DEST/tool-pins-signing.pem.enc" -pass pass:"$PASS"

# Round-trip check before trusting the backup.
openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000 \
  -in "$DEST/tool-pins-signing.pem.enc" -pass pass:"$PASS" | cmp - "$KEY"

cat > "$DEST/README.txt" <<'EOF'
fetch-it: private key that signs scripts/tool-pins.json (ECDSA P-256).
AES-256-CBC, PBKDF2 600000 iterations. Passphrase lives in the password manager.

Decrypt:
  openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000 -in tool-pins-signing.pem.enc -out tool-pins-signing.pem
Sign pins:
  node scripts/sign-tool-pins.mjs --key tool-pins-signing.pem
EOF

echo "OK -> $DEST/tool-pins-signing.pem.enc"
echo "PASSPHRASE (store it now, it is not shown again): $PASS"
