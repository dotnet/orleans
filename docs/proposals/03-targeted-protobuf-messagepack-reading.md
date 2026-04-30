# Proposal 3: Targeted protobuf and MessagePack recovery reading

## Status

Draft

## Problem

The protobuf and MessagePack physical readers currently create a slice over the full unread recovery buffer before parsing one entry. This is simple, but it can add avoidable buffer slicing/ref-count overhead when storage returns large concatenated logs.

The Orleans binary reader is more targeted: it reads enough prefix bytes to determine one frame length, then peeks and consumes only that frame.

## Goals

- Reduce per-entry recovery overhead for protobuf and MessagePack.
- Keep the physical format contracts unchanged.
- Preserve partial-read behavior when storage chunks split entries.
- Preserve clear malformed-data errors.

## Proposed design

Refactor protobuf and MessagePack physical readers to parse entry prefixes directly from `ArcBufferReader` and slice only the completed frame or payload.

### Protobuf

The physical format is:

```text
entry := varuint32 messageLength
         protobuf LogEntry message
```

where:

```protobuf
message LogEntry {
  uint64 stream_id = 1;
  bytes payload = 2;
}
```

The reader should:

1. peek only enough bytes to decode the `varuint32` length prefix,
2. return `false` if the prefix or message is incomplete and `isCompleted == false`,
3. throw if incomplete and `isCompleted == true`,
4. slice only `prefixLength + messageLength`,
5. parse `stream_id` and `payload` from that bounded message,
6. skip exactly the consumed frame length.

### MessagePack

The physical format is:

```text
entry := [streamId, payload]
```

The reader should:

1. parse the array header from `ArcBufferReader`,
2. parse `streamId`,
3. parse the binary payload header,
4. ensure the full payload is available,
5. slice only the payload bytes,
6. skip exactly the consumed entry length.

## Implementation notes

`ArcBufferReader` already supports `Peek`, `TryPeek`, `PeekSlice`, `TryReadExact`, `Skip`, and delimiter/slice helpers. If direct parsing becomes awkward, introduce a small internal helper that mirrors the subset of `SequenceReader<byte>` needed by physical readers:

```csharp
internal ref struct ArcSequenceReader
{
    public long Consumed { get; }
    public bool TryRead(out byte value);
    public bool TryCopyTo(Span<byte> destination);
    public bool TryReadExact(int count, out ArcBuffer slice);
}
```

Avoid introducing generic "parse everything into a frame object list" behavior.

## Benefits

- Less recovery overhead for large concatenated logs.
- More consistent reader design across binary, protobuf, and MessagePack.
- Better foundation for future streaming storage reads.

## Costs and risks

- More specialized parser code.
- Potential duplication with existing `SequenceReader<byte>` helper logic.
- Needs careful partial-data testing.

## Validation

- Existing protobuf and MessagePack recovery tests continue to pass.
- Add tests for entries split at every byte boundary.
- Add malformed-data tests for partial prefix, partial stream id, partial payload header, partial payload, invalid array/header types, and trailing data.
- Benchmark recovery with large concatenated logs before and after.

## Recommendation

Implement independently after higher-level dispatch design decisions. The change is localized and should be measurable with targeted benchmarks.
