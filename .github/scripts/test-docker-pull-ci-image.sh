#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "$0")" && pwd)"
temp_dir="$(mktemp -d)"
trap 'rm -rf "$temp_dir"' EXIT

cat > "$temp_dir/docker" <<'EOF'
#!/usr/bin/env bash
echo "$*" >> "$DOCKER_CALLS"
if [ "$DOCKER_RESULT" = "mirror" ] && [ "$2" = "mirror" ]; then
  exit 0
fi
if [ "$DOCKER_RESULT" = "upstream" ] && [ "$2" = "upstream" ]; then
  exit 0
fi
exit 1
EOF
cat > "$temp_dir/sleep" <<'EOF'
#!/usr/bin/env bash
exit 0
EOF
chmod +x "$temp_dir/docker" "$temp_dir/sleep"
export PATH="$temp_dir:$PATH"
export DOCKER_CALLS="$temp_dir/calls"
export GITHUB_ENV="$temp_dir/env"

export DOCKER_RESULT=mirror
bash "$script_dir/docker-pull-ci-image.sh" mirror upstream TEST_IMAGE > "$temp_dir/output" 2>&1
test "$(cat "$GITHUB_ENV")" = "TEST_IMAGE=mirror"
test "$(cat "$DOCKER_CALLS")" = "pull mirror"

: > "$GITHUB_ENV"
: > "$DOCKER_CALLS"
export DOCKER_RESULT=upstream
bash "$script_dir/docker-pull-ci-image.sh" mirror upstream TEST_IMAGE > "$temp_dir/output" 2>&1
test "$(cat "$GITHUB_ENV")" = "TEST_IMAGE=upstream"
test "$(cat "$DOCKER_CALLS")" = "$(printf 'pull mirror\npull upstream')"
grep -Fq 'Mirror pull failed' "$temp_dir/output"

: > "$GITHUB_ENV"
: > "$DOCKER_CALLS"
export DOCKER_RESULT=neither
if bash "$script_dir/docker-pull-ci-image.sh" mirror upstream TEST_IMAGE > "$temp_dir/output" 2>&1; then
  echo "The image step succeeded without an available image." >&2
  exit 1
fi
test ! -s "$GITHUB_ENV"
test "$(grep -c '^pull upstream$' "$DOCKER_CALLS")" -eq 4

ci="$script_dir/../workflows/ci.yml"
publisher="$script_dir/../workflows/mirror-ci-images.yml"
fixture="$script_dir/../../test/Extensions/Orleans.Clustering.Consul.Tests/ConsulTestUtils.cs"
ci_image() { sed -n "s/^  $1: //p" "$ci"; }

consul_source="$(ci_image CONSUL_IMAGE)"
consul_mirror="$(ci_image CONSUL_MIRROR_IMAGE)"
ryuk_source="$(ci_image TESTCONTAINERS_RYUK_IMAGE)"
ryuk_mirror="$(ci_image TESTCONTAINERS_RYUK_MIRROR_IMAGE)"
test -n "$consul_source" && test -n "$consul_mirror"
test -n "$ryuk_source" && test -n "$ryuk_mirror"
grep -Fq -- "\"$consul_source\"" "$fixture"
grep -Fq -- "source: docker.io/hashicorp/consul@${consul_mirror##*@}" "$publisher"
grep -Fq -- "destination: ${consul_mirror%%@*}" "$publisher"
grep -Fq -- "source: docker.io/testcontainers/ryuk@${ryuk_mirror##*@}" "$publisher"
grep -Fq -- "destination: ${ryuk_mirror%%@*}" "$publisher"

echo "CI image selection tests passed."
