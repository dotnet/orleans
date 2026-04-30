# Proposal 2: Pull-based log entry reader

## Status

Draft

## Problem

`ILogFormat.TryRead` currently pushes entries into `ILogEntrySink`:

```csharp
bool TryRead(ArcBufferReader input, ILogEntrySink sink, bool isCompleted);
```

This makes the physical format responsible for both parsing and callback timing. `LogManager` owns routing, but it only sees entries after a callback from the format. This makes recovery harder to follow and makes future frame metadata awkward to add.

## Goals

- Make the recovery loop read like "format parses, manager routes".
- Remove one callback hop from the recovery path.
- Preserve the current raw payload model.
- Provide a better foundation for adding frame metadata.

## Proposed design

Add a frame result type:

```csharp
public readonly struct LogEntryFrame
{
    public LogEntryFrame(LogStreamId streamId, ReadOnlySequence<byte> payload)
    {
        StreamId = streamId;
        Payload = payload;
    }

    public LogStreamId StreamId { get; }
    public ReadOnlySequence<byte> Payload { get; }
}
```

Add a pull-style format interface:

```csharp
public interface ILogFormatReader
{
    bool TryRead(
        ArcBufferReader input,
        bool isCompleted,
        out LogEntryFrame frame);
}
```

`LogManager` prefers the pull interface when available:

```csharp
private void ProcessRecoveryBuffer(ArcBufferReader reader, bool isCompleted)
{
    if (_logFormat is ILogFormatReader frameReader)
    {
        while (frameReader.TryRead(reader, isCompleted, out var frame))
        {
            OnEntry(frame.StreamId, frame.Payload);
        }
    }
    else
    {
        while (_logFormat.TryRead(reader, this, isCompleted))
        {
        }
    }

    if (isCompleted && reader.Length > 0)
    {
        throw new InvalidOperationException("The log format did not consume the completed log data.");
    }
}
```

After all built-in formats implement `ILogFormatReader`, the original sink method can be retained for compatibility or deprecated in a later major version.

## Benefits

- Recovery control flow becomes more direct.
- `LogManager` owns the loop and the dispatch point.
- Future metadata can be added to `LogEntryFrame`, for example physical offset, frame length, or diagnostics.
- This is compatible with existing raw payload operation codecs.

## Costs and risks

- Does not solve JSON double parsing by itself.
- Adds a second interface during migration.
- Built-in formats need small adapter changes.

## Interaction with codec-aware dispatch

If Proposal 1 is implemented, the frame type could later gain an optional typed payload:

```csharp
public readonly struct LogEntryFrame
{
    public LogStreamId StreamId { get; }
    public ReadOnlySequence<byte> Payload { get; }
    public object? TypedPayload { get; }
}
```

This is not recommended as the first implementation because `object` weakens type safety and introduces lifetime concerns for values such as `JsonElement`. Format-specific sink interfaces are a cleaner first step.

## Validation

- Add tests proving `LogManager` uses `ILogFormatReader` when present.
- Keep existing sink-path tests for custom formats.
- Run malformed-data tests for all built-in formats.

## Recommendation

Treat this as an API cleanup, not the first performance fix. Proposal 1 gives the more direct JSON recovery improvement.
