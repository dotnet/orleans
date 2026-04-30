# Zero-allocation code and format-owned writer design

## Summary

Orleans Journaling writes durable mutations directly into a format-owned log extent writer. The selected storage exposes a `LogFormatKey`, the manager resolves the matching keyed `ILogFormat`, and the format creates the `ILogSegmentWriter` which owns physical entry framing from the first byte written.

Durable state machines do not build a generic payload and ask storage to wrap it later. They write one durable operation payload through `LogEntry.Writer`, call `LogEntry.Commit()`, and only then mutate in-memory state. Disposing an uncommitted `LogEntry` aborts the entry and truncates the active extent writer to the entry start.

Storage is intentionally format-agnostic. It persists and returns raw `ArcBuffer` data, exposes the configured `LogFormatKey`, and must not retain a borrowed buffer after append or replace completes.

## Goals

1. No steady-state heap allocations from journaling infrastructure on hot mutations such as `IDurableList<T>.Add(T value)`, assuming buffers and collections already have capacity.
2. No command wrapper objects, delegate closures, tuple state, or per-call durable-operation `byte[]` payloads in built-in hot paths.
3. No materialized list of log entries during recovery; formats parse persisted bytes and push decoded entry payloads to the manager.
4. Keep codec families cohesive and keyed: selecting Orleans binary, JSON, protobuf, or MessagePack selects the physical log format, durable operation codecs, and value codecs together.
5. Make storage boundaries explicit through `ArcBuffer` ownership and `GetCommittedBuffer()`.
6. Preserve retired or unknown state-machine entries without exposing raw physical-byte append APIs to normal durable state machines.

## Non-goals

1. Avoiding allocations caused by user value construction during recovery.
2. Avoiding growth allocations in in-memory `List<T>`, `Dictionary<TKey,TValue>`, `HashSet<T>`, `Queue<T>`, or user collections.
3. Guaranteeing that every custom value codec is allocation-free.
4. Making JSON the default performance format. JSON Lines is readable and debuggable; Orleans binary, protobuf-native value codecs, and MessagePack are the allocation-sensitive formats.
5. Supporting automatic recovery of older WIP physical formats. Persisted data must match the configured `LogFormatKey`.

## Public API shape

### Log format

```csharp
public interface ILogFormat
{
    ILogSegmentWriter CreateWriter();
    void Read(ArcBuffer input, ILogEntrySink consumer);
}
```

A log format owns physical framing. It creates writers for new extents and reads raw persisted bytes back into `(LogStreamId, payload)` entries. It does not apply durable operations itself.

### Extent writer

```csharp
public interface ILogSegmentWriter : IDisposable
{
    long Length { get; }
    LogWriter CreateLogWriter(LogStreamId streamId);
    ArcBuffer GetCommittedBuffer();
    void Reset();
}
```

The manager owns the extent writer. `Length` is the raw current buffer length and can include an active uncommitted entry. Storage-safe code must call `GetCommittedBuffer()`, which returns only committed bytes and throws if an entry is active. The returned `ArcBuffer` is borrowed by the storage call; the caller disposes it after the append or replace operation returns.

Formats can implement the interface directly or derive from `LogSegmentWriterBase` when they need the shared lexical-entry machinery.

### State-machine writers and entry scope

```csharp
public readonly struct LogWriter
{
    public LogEntry BeginEntry();
}

public ref struct LogEntry
{
    public LogEntryWriter Writer { get; }
    public void Commit();
    public void Dispose();
}
```

`LogWriter` is the out-of-band writer exposed to a durable state machine instance via `IDurableStateMachine.Reset`, and the batch/snapshot writer passed to `IDurableStateMachine.AppendEntries` and `AppendSnapshot`.

`LogEntry` is the lexical lifetime boundary. `Commit()` finalizes one pending entry. `Dispose()` aborts only if commit did not happen. Completing an entry twice is invalid. Normal code should not call public append-entry convenience APIs; durable operation codecs write only through the payload writer.

The former separate state-machine writer APIs are removed. Use `LogWriter`.

### Payload writer

```csharp
public sealed class LogEntryWriter : IBufferWriter<byte>
{
    public void Advance(int count);
    public Memory<byte> GetMemory(int sizeHint = 0);
    public Span<byte> GetSpan(int sizeHint = 0);
    public void Write(ReadOnlySpan<byte> value);
    public void Write(ReadOnlySequence<byte> value);
    public void WriteVarUInt32(uint value);
    public void WriteVarUInt64(ulong value);
}
```

`LogEntryWriter` is payload-only. It has no public `Commit` or `Abort`; lifecycle belongs to `LogEntry`. Codecs must not retain the writer or memory obtained from it after the write call returns.

### Storage

```csharp
public interface ILogStorage
{
    string LogFormatKey { get; }
    ValueTask ReadAsync(ILogDataSink consumer, CancellationToken cancellationToken);
    ValueTask ReplaceAsync(ArcBuffer value, CancellationToken cancellationToken);
    ValueTask AppendAsync(ArcBuffer value, CancellationToken cancellationToken);
    ValueTask DeleteAsync(CancellationToken cancellationToken);
    bool IsCompactionRequested { get; }
}
```

`AppendAsync` and `ReplaceAsync` accept encoded log bytes, not a format-specific writer or decoded entries. `ReadAsync` pushes raw persisted data to `ILogDataSink`; the manager then invokes the selected `ILogFormat.Read(...)` method.

Storage options use a default `LogFormatKey` and may expose a `Func<GrainType, string>` selector. The selector is grain-type scoped, not grain-id scoped. The built-in keys are defined by `LogFormatKeys`: `orleans-binary`, `json`, `protobuf`, and `messagepack`.

The key is configuration, not persisted metadata. Switching a grain to a different key over existing data is a migration problem and should fail recovery clearly.

## Write path

A durable mutation should validate first, encode and commit the log entry, then update in-memory state:

```csharp
public void Add(T item)
{
    using var entry = _logWriter.BeginEntry();
    _codec.WriteAdd(item, entry.Writer);
    entry.Commit();

    ApplyAdd(item);
}
```

If `_codec.WriteAdd` throws, disposing `entry` aborts the pending physical frame and the in-memory collection remains unchanged. Operations which return existing data, such as dequeue, should read and validate the result before writing the entry, then mutate after commit.

Snapshots use the same scoped writer API through `LogWriter`:

```csharp
void IDurableStateMachine.AppendSnapshot(LogWriter writer)
{
    using var entry = writer.BeginEntry();
    _codec.WriteSnapshot(_items, entry.Writer);
    entry.Commit();
}
```

## Recovery path

Storage pushes raw data blocks to the manager. The manager passes each block to the active log format, and the format parses physical framing:

```csharp
void ILogDataSink.OnLogData(ArcBuffer data)
{
    _logFormat.Read(data, this);
}

void ILogEntrySink.OnEntry(LogStreamId streamId, ReadOnlySequence<byte> payload)
{
    if (_stateMachinesMap.TryGetValue(streamId.Value, out var stateMachine))
    {
        stateMachine.Apply(payload);
    }
    else
    {
        PreserveRetiredStateMachineEntry(streamId, payload);
    }
}
```

The manager owns reserved runtime id semantics. Physical readers validate framing and dispatch ids and payloads; they do not interpret durable operation commands or runtime state-machine ids.

## Physical formats

### Orleans binary

The built-in binary format is a concatenated stream of fixed32-framed entries:

```text
entry := fixed32-little-endian body-length
         varuint64 state-machine-id
         durable-operation-payload
```

The length covers the state-machine id plus payload. The writer reserves four bytes, writes the id and payload, and backpatches the length on commit. Abort truncates to the entry start.

### JSON Lines

JSON uses true JSON Lines. Each log entry is one UTF-8 JSON object line terminated by `\n`:

```json
{"streamId":8,"entry":{"cmd":"set","key":"alpha","value":1}}
```

A storage batch can contain multiple JSON Lines records, but there is no surrounding extent object and no final container-close step. The JSON log writer owns the `streamId` property, `entry` property framing, object close, and newline. JSON durable operation codecs write only the JSON value for the `entry` property, typically using `Utf8JsonWriter` over `LogEntry.Writer`.

Recovery reads one line at a time, validates that each line is a complete object with `streamId` and `entry`, and passes the raw `entry` value bytes to the durable operation codec. UTF-8 byte order marks and blank lines are malformed journal data.

### Protocol Buffers

The protobuf physical format is repeated length-delimited `LogEntry` messages with no surrounding extent envelope:

```protobuf
message LogEntry {
  uint64 stream_id = 1;
  bytes payload = 2;
}
```

Recovery reads length-delimited messages until the input is exhausted. The durable operation payload remains an opaque bytes field at the physical layer. The writer may use pooled scratch space for the current payload because protobuf length-delimited fields require lengths before bodies; that scratch space must stay format-owned and must not reintroduce generic post-hoc extent encoding.

### MessagePack

The MessagePack physical format is repeated standalone entry arrays:

```text
entry := [streamId, payload]
```

`streamId` is an unsigned integer and `payload` is a binary value containing the durable operation payload. There is no outer array or extent envelope. Recovery reads arrays until the input is exhausted. The writer may use format-owned scratch space to determine the binary payload header, but not a generic completed extent builder.

## Retired state-machine preservation

Retired or temporarily unknown state machines are a rare recovery path and are outside the steady-state allocation contract. Preservation stores the state-machine id plus the decoded durable operation payload bytes produced by the active log format. It does not preserve exact physical extent bytes, whitespace, field order, or storage block boundaries.

Write-back uses an explicit internal helper, `LogWriter.AppendPreservedDecodedPayload(...)`, which opens a normal `LogEntry`, copies the preserved payload into `entry.Writer`, and commits. This keeps retired-entry preservation on the same format-owned writer path as normal writes while preventing raw physical-byte append APIs from becoming a public durable state machine surface.

## Retired APIs

The previous builder/extent-codec path is historical only. Built-in code must not reintroduce `LogExtentBuilder`, `IStateMachineLogExtentCodec`, `Encode(LogExtentBuilder)`, or `EncodeToStream(LogExtentBuilder)`. Storage should not decode log entries, call a format codec, or ask a completed generic builder to produce bytes.

## Validation strategy

Do not rely on exact allocation-count assertions for correctness. Validate the shape with functional tests, malformed-data tests, API projections, targeted benchmarks, and review:

1. Mutations encode and commit before in-memory state changes.
2. Dispose aborts incomplete entries and leaves later writes valid.
3. `GetCommittedBuffer()` never exposes active entries.
4. Storage append/replace consumes borrowed `ArcBuffer` data and does not retain it.
5. Recovery streams entries without materialized `LogExtent.Entry` lists.
6. JSON Lines, protobuf, and MessagePack readers parse concatenated entries and reject malformed framing.
7. Retired-entry preservation copies decoded payload bytes through the normal scoped writer path.

`test\Benchmarks\Journaling\DurableListJournalBenchmarks.cs` covers the allocation-sensitive representative paths without making correctness depend on exact allocation counts:

1. `DurableListAddWritesDirectEntry` warms collection capacity, uses a direct integer value codec, and repeatedly calls `DurableList<int>.Add`. The benchmark exercises `LogEntry`/`LogEntryWriter`/format-owned extent writing instead of allocating command objects or completing a generic builder for post-hoc extent framing.
2. `RecoverEncodedLogData` replays pre-encoded binary log data through `ILogFormat.Read` into a durable list consumer. The benchmark validates the raw `ArcBuffer` read path and push-based entry dispatch rather than materializing a `LogExtent` or `LogExtent.Entry` collection.

The benchmark uses BenchmarkDotNet's memory diagnoser as review evidence. It intentionally avoids pass/fail byte thresholds because allocation counts vary by runtime, architecture, JIT tiering, and BenchmarkDotNet job configuration.
