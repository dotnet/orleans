#!/usr/bin/env bash
# Regenerates the trace corpus which the .NET conformance harness replays
# (test/Extensions/Orleans.Journaling.Garnet.Tests/Conformance).
#
# Simulates the specification with the coordinator's rule (`step`)
# and the implementation's recent-ETag count (`garnetRangeMigrationTraces`),
# exports model-based-testing traces (each state records `mbt::actionTaken` and
# `mbt::nondetPicks`), and keeps a small corpus: half the traces raise a collection
# generation, and the rest exercise consolidation, reclamation, and rejected pulls
# or opens, delayed attempts, or marker collection.
#
# Usage: ./generate-traces.sh [count] [seed]
set -euo pipefail

count="${1:-12}"
seed="${2:-11}"
here="$(cd "$(dirname "$0")" && pwd)"
out="$here/../../test/Extensions/Orleans.Journaling.Garnet.Tests/Conformance/traces"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

cd "$here"
quint run garnetRangeMigrationTraces.qnt --main=garnetRangeMigrationTraces \
  --mbt --max-steps=40 --max-samples=400 --n-traces=400 --seed="$seed" \
  --out-itf="$work/trace_{seq}.itf.json" > "$work/run.log"

rm -rf "$out"
mkdir -p "$out"
kept=0
keep() {
  kept=$((kept + 1))
  cp "$1" "$out/trace_$(printf '%02d' "$kept").itf.json"
  rm "$1"
}

# Half the corpus raises a collection generation (a tombstone dropped).
for trace in $(ls "$work"/trace_*.itf.json | sort -V); do
  if [ "$kept" -ge $((count / 2)) ]; then
    break
  fi

  if grep -q '"gen":{"#bigint":"[1-9]' "$trace"; then
    keep "$trace"
  fi
done

for trace in $(ls "$work"/trace_*.itf.json | sort -V); do
  if [ "$kept" -ge "$count" ]; then
    break
  fi

  if grep -q '"reclaimBatchAny"' "$trace" && grep -q '"completeAny"' "$trace" \
     && grep -qE '"(dropPullAny|dropTicketAny|dropDoneAny|dropReclaimAny|deliverAttempt|dropMarkerAny)"' "$trace"; then
    keep "$trace"
  fi
done

echo "Kept $kept traces in $out"
