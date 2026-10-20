#!/usr/bin/env bash
# Basic health probes for the media stack. Usage: health-check.sh <livekit-host> <turn-host>
# Exit code 0 only if every probe passes. Checks reachability only; a real media test is in docs/operations/media-acceptance.md.
set -u
LK="${1:?livekit host}"; TURN="${2:?turn host}"
fail=0
check() { if "${@:2}"; then echo "OK   $1"; else echo "FAIL $1"; fail=1; fi; }
check "livekit http (7880)"   curl -fsS --max-time 5 "http://$LK:7880/" -o /dev/null
check "livekit rtc tcp (7881)" bash -c "exec 3<>/dev/tcp/$LK/7881"
check "turn tcp (3478)"       bash -c "exec 3<>/dev/tcp/$TURN/3478"
check "turn tls (5349)"       bash -c "echo | openssl s_client -connect $TURN:5349 -brief >/dev/null 2>&1"
exit $fail
