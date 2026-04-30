# Proposal 4: Reduce protobuf and MessagePack write-path payload copying

## Status

Draft

## Problem

The Orleans binary and JSON physical writers write durable operation payload bytes directly into the final segment. Protobuf and MessagePack currently write the operation payload into a format-owned scratch buffer first because their physical framing needs payload lengths before payload bytes.

Current shape:

```text
operation codec -> scratch payload buffer -> final segment buffer
```

This adds a copy on commit.

## Goals

- Evaluate whether protobuf and MessagePack can write payloads directly into the final segment.
- Preserve existing physical format compatibility unless a deliberate versioned change is chosen.
- Avoid complicating the hot path unless benchmarks show a clear win.

## MessagePack options

### Option A: always use `bin32`

Write directly into the final segment and backpatch a fixed-size payload length:

```text
[streamId, bin32 payloadLength, payload]
```

Benefits:

- Simple direct-write implementation.
- No scratch payload buffer.
- Commit only backpatches length.

Costs:

- Larger payload header for small payloads.
- Encoded output changes from shortest-form MessagePack to a valid but less compact form.

### Option B: reserve max header and compact on commit

Reserve enough space for the largest binary header, write payload directly, then shift payload bytes backward on commit if `bin8` or `bin16` is sufficient.

Benefits:

- Retains compact encoding.
- Avoids scratch buffer for some cases.

Costs:

- Commit can move payload bytes.
- More complex and may be slower for medium/large payloads.

### Option C: keep scratch buffering

Keep the current design if benchmarks show the copy is not material or compact output matters more.

## Protobuf options

The protobuf physical format is harder to direct-write because it has:

1. an outer length-delimited message prefix, and
2. an inner `bytes payload` length prefix.

Potential approaches:

- reserve maximum varint prefix space and compact/backpatch on commit,
- use a non-canonical but valid encoding if possible,
- keep the current scratch buffer.

The current scratch-buffer design may be the right tradeoff for protobuf unless benchmarks show a significant cost.

## Benefits

- Potentially reduces write-path copy overhead.
- MessagePack may become closer to Orleans binary in hot mutation behavior.

## Costs and risks

- Writer code becomes more complex.
- Encoded size may increase if using fixed `bin32`.
- Protobuf direct-write support may not justify the complexity.
- Must preserve abort semantics: disposing an uncommitted `LogEntry` must truncate to the entry start.

## Validation

- Add writer tests for commit, abort, reset, and active-entry errors.
- Add round-trip tests for small, medium, and large payload lengths.
- Compare encoded size for representative workloads.
- Benchmark hot mutations and snapshot writes before and after.

## Recommendation

Prototype MessagePack Option A behind benchmarks. Keep protobuf scratch buffering unless measurements show it is a bottleneck.
