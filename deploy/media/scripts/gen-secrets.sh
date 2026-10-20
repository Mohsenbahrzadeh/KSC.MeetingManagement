#!/usr/bin/env bash
# Prints fresh secrets for deploy/media/.env and for the API settings. Nothing is written to disk.
set -euo pipefail
echo "LIVEKIT_API_KEY=API$(openssl rand -hex 6)"
echo "LIVEKIT_API_SECRET=$(openssl rand -base64 48 | tr -d '\n=+/' | cut -c1-48)"
echo "TURN_SECRET=$(openssl rand -base64 48 | tr -d '\n=+/' | cut -c1-48)"
echo "# API (environment variables): Media__ServerUrl, Media__ApiKey, Media__ApiSecret (= the two LIVEKIT values), Turn__Secret (= TURN_SECRET)"
