#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -lt 1 ] || [ "$#" -gt 2 ] || { [ "$#" -eq 2 ] && [ -z "${GITHUB_ENV:-}" ]; }; then
  echo "Usage: $0 <image-name> [testcontainers-image-variable (requires GITHUB_ENV)]" >&2
  exit 2
fi

script_dir="$(cd "$(dirname "$0")" && pwd)"
image_json="$(jq -ce --arg id "$1" '.[$id] // error("Unknown CI image: \($id)")' "$script_dir/../ci-images.json")"
mirror="$(jq -er '.mirror' <<< "$image_json")"
upstream="$(jq -er '.source' <<< "$image_json")"

# Try the mirror once so private-package bootstrap does not delay unauthenticated PR jobs.
if docker pull "$mirror" >&2; then
  image="$mirror"
else
  echo "::warning::Mirror pull failed for ${mirror}; retrying the pinned upstream image ${upstream}." >&2
  bash "$script_dir/docker-pull-with-retry.sh" "$upstream" >&2
  image="$upstream"
fi

if [ "$#" -eq 2 ]; then
  echo "${2}=${image}" >> "$GITHUB_ENV"
fi
printf '%s\n' "$image"
