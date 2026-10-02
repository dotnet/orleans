#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "$0")" && pwd)"
manifest="$script_dir/../ci-images.json"
temp_dir="$(mktemp -d)"
trap 'rm -rf "$temp_dir"' EXIT

cat > "$temp_dir/docker" <<'EOF'
#!/usr/bin/env bash
echo "$*" >> "$DOCKER_CALLS"
case "$DOCKER_RESULT:$2" in
  "mirror:$MIRROR_IMAGE"|"upstream:$UPSTREAM_IMAGE") exit 0 ;;
esac
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
export MIRROR_IMAGE="$(jq -r '.ryuk.mirror' "$manifest")"
export UPSTREAM_IMAGE="$(jq -r '.ryuk.source' "$manifest")"

export DOCKER_RESULT=mirror
selected="$(bash "$script_dir/docker-pull-ci-image.sh" ryuk TEST_IMAGE 2>"$temp_dir/output")"
test "$selected" = "$MIRROR_IMAGE"
test "$(cat "$GITHUB_ENV")" = "TEST_IMAGE=$MIRROR_IMAGE"
test "$(cat "$DOCKER_CALLS")" = "pull $MIRROR_IMAGE"

: > "$GITHUB_ENV"
: > "$DOCKER_CALLS"
export DOCKER_RESULT=upstream
selected="$(bash "$script_dir/docker-pull-ci-image.sh" ryuk TEST_IMAGE 2>"$temp_dir/output")"
test "$selected" = "$UPSTREAM_IMAGE"
test "$(cat "$GITHUB_ENV")" = "TEST_IMAGE=$UPSTREAM_IMAGE"
test "$(cat "$DOCKER_CALLS")" = "$(printf 'pull %s\npull %s' "$MIRROR_IMAGE" "$UPSTREAM_IMAGE")"
grep -Fq 'Mirror pull failed' "$temp_dir/output"

: > "$GITHUB_ENV"
: > "$DOCKER_CALLS"
export DOCKER_RESULT=neither
if bash "$script_dir/docker-pull-ci-image.sh" ryuk TEST_IMAGE > "$temp_dir/output" 2>&1; then
  echo "The image step succeeded without an available image." >&2
  exit 1
fi
test ! -s "$GITHUB_ENV"
test "$(grep -Fc "pull $UPSTREAM_IMAGE" "$DOCKER_CALLS")" -eq 4

: > "$DOCKER_CALLS"
if bash "$script_dir/docker-pull-ci-image.sh" unknown-image > "$temp_dir/output" 2>&1; then
  echo "An unknown image was accepted." >&2
  exit 1
fi
test ! -s "$DOCKER_CALLS"

export DOCKER_RESULT=mirror
unset GITHUB_ENV
selected="$(bash "$script_dir/docker-pull-ci-image.sh" ryuk 2>"$temp_dir/output")"
test "$selected" = "$MIRROR_IMAGE"

jq -e '
  type == "object" and length > 0 and
  all(.[]; (.source | test("@sha256:[0-9a-f]{64}$")) and
    (.copy_source | test("@sha256:[0-9a-f]{64}$")) and
    (.mirror | test("^ghcr[.]io/dotnet/orleans-ci-test-images:[^@]+@sha256:[0-9a-f]{64}$")) and
    (.copy_source | split("@")[1]) == (.mirror | split("@")[1]))
' "$manifest" > /dev/null

ci="$script_dir/../workflows/ci.yml"
fixture="$script_dir/../../test/Extensions/Orleans.Clustering.Consul.Tests/ConsulTestUtils.cs"
seaweed_fixture="$script_dir/../../test/Orleans.Journaling.Tests/SeaweedFSTestContainer.cs"
consul_source="$(sed -n 's/^  CONSUL_IMAGE: //p' "$ci")"
consul_fallback="$(sed -n 's/^  CONSUL_FALLBACK_IMAGE: //p' "$ci")"
test -n "$consul_source" && test -n "$consul_fallback"
test "${consul_source##*@}" = "${consul_fallback##*@}"
grep -Fq -- "\"$consul_source\"" "$fixture"
grep -Fq -- "\"$(jq -r '.seaweedfs.source' "$manifest")\"" "$seaweed_fixture"
grep -Fq 'ORLEANS_SEAWEEDFS_TEST_IMAGE' "$seaweed_fixture"
grep -Fq 'ORLEANS_CASSANDRA_TEST_IMAGE' "$script_dir/../../test/Extensions/Orleans.Clustering.Cassandra.Tests/Clustering/CassandraContainer.cs"

while IFS= read -r name; do
  name="${name%$'\r'}"
  case "$name" in
    cassandra-*) grep -Fq 'docker-pull-ci-image.sh "cassandra-${{ matrix.dbversion }}"' "$ci" ;;
    *) grep -Fq "docker-pull-ci-image.sh $name" "$ci" ;;
  esac
done < <(jq -r 'keys[]' "$manifest")

echo "CI image selection tests passed."
