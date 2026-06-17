#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -lt 1 ] || [ "$#" -gt 3 ]; then
  echo "Usage: $0 <container-with-health-check> [timeout-seconds] [poll-interval-seconds]" >&2
  exit 2
fi

container="$1"
timeout_seconds="${2:-60}"
poll_interval_seconds="${3:-2}"
deadline=$((SECONDS + timeout_seconds))

while true; do
  status="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "$container")"

  case "$status" in
    healthy)
      exit 0
      ;;
    unhealthy)
      echo "Container ${container} is unhealthy." >&2
      docker logs "$container" >&2 || true
      exit 1
      ;;
    starting)
      ;;
    none)
      echo "Container ${container} does not define a Docker health check." >&2
      docker inspect "$container" >&2 || true
      exit 1
      ;;
    *)
      echo "Container ${container} has unexpected health status: ${status}" >&2
      docker inspect "$container" >&2 || true
      exit 1
      ;;
  esac

  if [ "$SECONDS" -ge "$deadline" ]; then
    echo "Container ${container} did not become healthy within ${timeout_seconds}s." >&2
    docker logs "$container" >&2 || true
    exit 1
  fi

  sleep "$poll_interval_seconds"
done
