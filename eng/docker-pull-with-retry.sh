#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -ne 1 ]; then
  echo "Usage: $0 <image>" >&2
  exit 2
fi

image="$1"
attempt=1

for delay in 10 30 60; do
  if docker pull "$image"; then
    exit 0
  fi

  echo "Docker pull failed for ${image} on attempt ${attempt}; retrying in ${delay}s..." >&2
  sleep "$delay"
  attempt=$((attempt + 1))
done

docker pull "$image"
