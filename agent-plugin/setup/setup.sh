#!/usr/bin/env bash
# Athar agent setup for macOS and Linux (T132): curl -fsSL {{BASE_URL}}/agent/setup.sh | bash
# Asks for your access token without echoing it, so it never reaches your shell history.
set -euo pipefail
BASE="{{BASE_URL}}" SERVER="{{SERVER_URL}}" MCP="{{MCP_URL}}"

command -v node >/dev/null || { echo "Athar setup needs Node 22.15 or later: https://nodejs.org" >&2; exit 1; }
node -e 'const [a,b]=process.versions.node.split(".").map(Number);process.exit(a>22||a==22&&b>=15?0:1)' \
  || { echo "Athar setup needs Node 22.15 or later (you have $(node --version))." >&2; exit 1; }

echo "Create an access token on the Agents page ($BASE/agents), then paste it here."
read -rsp "Athar access token: " ATHAR_TOKEN < /dev/tty; echo
export ATHAR_TOKEN

TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
curl -fsSL "$BASE/agent/bundle.zip" -o "$TMP/bundle.zip"
if command -v unzip >/dev/null; then unzip -q "$TMP/bundle.zip" -d "$TMP/bundle"
else python3 -m zipfile -e "$TMP/bundle.zip" "$TMP/bundle"; fi
node --use-system-ca "$TMP/bundle/setup.mjs" "$TMP/bundle" "$SERVER" "$MCP"
