#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -ne 3 ] || [ -z "${GITHUB_ENV:-}" ]; then
  echo "Usage: GITHUB_ENV=<path> $0 <mirror-image> <upstream-image> <testcontainers-image-variable>" >&2
  exit 2
fi

mirror="$1"
upstream="$2"
variable="$3"

if docker pull "$mirror"; then
  image="$mirror"
else
  echo "::warning::Mirror pull failed for ${mirror}; retrying the pinned upstream image ${upstream}." >&2
  bash "$(dirname "$0")/docker-pull-with-retry.sh" "$upstream"
  image="$upstream"
fi

echo "${variable}=${image}" >> "$GITHUB_ENV"
