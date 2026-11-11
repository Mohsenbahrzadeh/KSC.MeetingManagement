#!/usr/bin/env bash
# Record Linux host and Docker container resource snapshots during a capacity run.
# Usage: sample-capacity.sh <output-file> [duration-seconds] [interval-seconds]
# Run on the media VM; keep output outside the repository if it contains operational details.
set -euo pipefail

output="${1:?usage: sample-capacity.sh <output-file> [duration-seconds] [interval-seconds]}"
duration="${2:-600}"
interval="${3:-5}"

is_positive_integer() { [[ "$1" =~ ^[1-9][0-9]*$ ]]; }
is_positive_integer "$duration" || { echo "duration must be a positive integer" >&2; exit 2; }
is_positive_integer "$interval" || { echo "interval must be a positive integer" >&2; exit 2; }
command -v docker >/dev/null || { echo "docker is required on the target host" >&2; exit 2; }
command -v free >/dev/null || { echo "free (procps) is required on the target host" >&2; exit 2; }

mkdir -p "$(dirname "$output")"
umask 077
end=$(( $(date +%s) + duration ))

while (( $(date +%s) < end )); do
  {
    printf '\n=== %s ===\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    printf '%s\n' '[host uptime/load]'
    uptime
    printf '%s\n' '[host memory]'
    free -h
    printf '%s\n' '[filesystem]'
    df -h /
    printf '%s\n' '[network bytes by interface]'
    cat /proc/net/dev
    printf '%s\n' '[docker container cpu/memory/network/io/pids]'
    docker stats --no-stream --format '{{.Name}},{{.CPUPerc}},{{.MemUsage}},{{.MemPerc}},{{.NetIO}},{{.BlockIO}},{{.PIDs}}'
  } >> "$output" 2>&1 || {
    printf 'sampling error at %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" >> "$output"
  }
  sleep "$interval"
done

echo "Resource samples saved to $output"
