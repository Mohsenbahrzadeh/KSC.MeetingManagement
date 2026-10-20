#!/usr/bin/env bash
# Fills ${VAR} placeholders of livekit.yaml and turnserver.conf from .env into *.rendered.* files (git-ignored; they contain secrets).
set -euo pipefail
cd "$(dirname "$0")/.."
set -a; . ./.env; set +a
for v in LIVEKIT_API_KEY LIVEKIT_API_SECRET API_PUBLIC_URL TURN_REALM TURN_EXTERNAL_IP TURN_SECRET; do
  [ -n "${!v:-}" ] || { echo "missing $v in .env" >&2; exit 1; }
done
envsubst < livekit.yaml > livekit.rendered.yaml
envsubst < turnserver.conf > turnserver.rendered.conf
chmod 600 livekit.rendered.yaml turnserver.rendered.conf
echo "rendered"
