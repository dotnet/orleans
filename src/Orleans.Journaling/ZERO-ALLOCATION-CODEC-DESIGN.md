# Zero-allocation log extent and entry codec design

## Summary

The durable collection write path should serialize a mutation directly into the current log buffer. For example, `IDurableList<T>.Add(T value)` should obtain an entry writer from the state-machine log writer, call `IDurableListCodec<T>.WriteAdd(value, writer)`, commit the entry, and only then update the in-memory list. It should not allocate a command object, wrapper object, byte array, delegate closure, intermediate protobuf payload, JSON document, or decoded log-entry object.

Recovery should reverse that flow. The log extent reader should enumerate physical log entries without materializing a `LogExtent.Entry` list. The manager should route each entry to the owning state machine. The durable collection codec should read the command tag and known argument shape from the entry buffer, then call an internal state-machine interface such as `IDurableListStateMachine<T>.ApplyAdd(value)`.

The API should preserve the current format-agnostic model, but it needs to move from "build an entry payload, then wrap it in an extent" to "the extent writer owns entry framing, while durable codecs write operation payloads directly into that frame."

## Goals

1. No steady-state heap allocations on mutation hot paths such as `IDurableList<T>.Add(T value)`, assuming the durable collection already has capacity and the log buffer can satisfy the write from pooled or existing memory.
2. No command classes, wrapper objects, boxed state, delegate closures, or per-call `byte[]` payloads.
3. No materialized entry list during recovery. Recovery should stream entries from an extent and dispatch them directly.
4. Keep codec families format-specific and AOT-friendly. Selecting JSON, protobuf, MessagePack, or Orleans binary selects the extent, entry, durable operation, and value codecs together.
5. Preserve physical extent information. JSONL, protobuf, MessagePack, and binary storage formats should still batch multiple logical entries into physical extents.
6. Keep the physical format simple while the PR is WIP; mixed old/new extent recovery is not supported.
7. Make allocation behavior obvious from the API shape and suitable for review or benchmarking.

## Non-goals

1. Avoiding allocations caused by the user's value type itself. If deserializing `T` creates a reference object, that is the data being recovered, not journaling overhead.
2. Avoiding collection growth allocations in the in-memory durable data structure. `List<T>`, `HashSet<T>`, `Dictionary<TKey,TValue>`, and `Queue<T>` still allocate when they need more capacity.
3. Guaranteeing that every possible user-provided format-specific value codec is allocation-free. The API should permit allocation-free codecs and the built-in codecs should use that path, but custom codecs can still allocate.
4. Making the human-readable JSON codec the default performance codec. JSONL is a readable/debug/interchange format with best-effort allocation reduction, while the strict zero-allocation path is Orleans binary, protobuf-native, and MessagePack.

## Allocation contract

Zero allocation means zero **steady-state journaling overhead**. After warm-up, with the durable collection and log buffer already holding sufficient capacity, a durable mutation such as `IDurableList<T>.Add(T value)` must not allocate because of journaling infrastructure or built-in codecs.

The contract excludes:

1. First-use work such as dependency injection, codec construction, serializer metadata initialization, and buffer pool growth.
2. In-memory durable collection growth.
3. Allocations performed by user-supplied value codecs.
4. Allocations inherent to creating recovered reference-type values during replay.
5. Rare recovery paths which must preserve entries for retired or temporarily unknown state machines.

Validation should focus on API shape, code review, and benchmarks rather than exact allocation assertions in the test suite.

## Remaining allocation pressure

The current branch has moved away from command wrapper classes and delegate append APIs for built-in state machines. The remaining friction is mostly in compatibility and non-hot-path surfaces:

1. `LogExtentBuilder` stores entry lengths in `List<uint> _entryLengths`. Appending a logical entry can grow that list, and encoding the extent later has to replay lengths into the physical format.
2. `IStateMachineLogExtentCodec.Encode(LogExtentBuilder value)` remains as a byte-array compatibility method. Storage writes should use `EncodeToStream` so binary/protobuf/MessagePack avoid this allocation.
3. `IStateMachineLogExtentCodec.Decode(ArcBuffer value)` remains as a compatibility method. Normal recovery should use `Read(ArcBuffer, IStateMachineLogEntryConsumer)`.
4. Fallback protobuf value codecs and JSON read paths can still allocate. Protobuf-native and MessagePack built-in paths avoid intermediate payload arrays; JSONL remains best-effort and readability-oriented.

## Proposed architecture

### Layering

The new model has three layers:

1. **Storage extent layer**: owns physical extent framing, buffering, and streaming to or from storage.
2. **State-machine entry layer**: owns one logical entry for one state machine inside an extent.
3. **Durable operation codec layer**: writes and reads commands such as list add, dictionary set, queue dequeue, value set, and snapshots.

The durable collection implementation should interact only with the state-machine entry writer and its durable operation codec. It should not know whether the physical extent is binary, JSONL, protobuf, or MessagePack.

### Write path

The steady-state write path for `DurableList<T>.Add` should look like this:

```csharp
public void Add(T item)
{
    var writer = GetStorage().BeginEntry();
    try
    {
        _codec.WriteAdd(item, writer);
        writer.Commit();
    }
    catch
    {
        writer.Abort();
        throw;
    }

    ApplyAdd(item);
}
```

Important properties:

1. `BeginEntry` returns a reusable class-based writer which implements `IBufferWriter<byte>`.
2. `WriteAdd` writes directly to the current entry payload.
3. `Commit` makes the entry visible in the current extent and backpatches any required length fields.
4. `Abort` truncates the buffer to the entry start if the codec throws.
5. No `Action<TState, IBufferWriter<byte>>`, no tuple state, no wrapper command object, and no intermediate command payload.

Mutation methods should validate before writing, encode and commit the pending log entry, and only then apply the in-memory mutation. If encoding fails, `Abort` removes the partial entry and the collection state is unchanged. Operations which need to return existing data, such as `Dequeue`, should pre-read and validate the result before writing the entry, then mutate and return after commit.

### Read path

Recovery should stream entries and dispatch them directly:

```csharp
while (extentReader.TryReadEntry(out var streamId, out var entryReader))
{
    if (_stateMachinesMap.TryGetValue(streamId.Value, out var stateMachine))
    {
        stateMachine.Apply(ref entryReader);
    }
    else
    {
        PreserveRetiredStateMachineEntry(streamId, ref entryReader);
    }
}
```

Retired or temporarily unknown state machines preserve the existing semantics: their entries can be copied into a retirement vessel so they can be written back during compaction or replayed if the state machine is reintroduced. This is a rare recovery/retirement path and is outside the steady-state journaling overhead contract.

`DurableList<T>` then delegates to its codec:

```csharp
void IDurableStateMachine.Apply(ref LogEntryReader reader)
{
    _codec.Apply(ref reader, this);
}
```

The codec reads the command and arguments:

```csharp
public void Apply(ref LogEntryReader reader, IDurableListStateMachine<T> stateMachine)
{
    var command = reader.ReadCommand();
    switch (command)
    {
        case ListCommand.Add:
            stateMachine.ApplyAdd(_valueCodec.Read(ref reader));
            break;
        case ListCommand.Set:
            stateMachine.ApplySet(reader.ReadInt32(), _valueCodec.Read(ref reader));
            break;
        case ListCommand.RemoveAt:
            stateMachine.ApplyRemoveAt(reader.ReadInt32());
            break;
        case ListCommand.Clear:
            stateMachine.ApplyClear();
            break;
        case ListCommand.Snapshot:
            ApplySnapshot(ref reader, stateMachine);
            break;
        default:
            ThrowUnsupportedCommand(command);
            break;
    }
}
```

No decoded `AddCommand<T>`, `Entry<T>`, `LogEntry`, or `LogExtent.Entry` object needs to exist.

## Core API shape

The exact names can change, but the shape should be close to this.

### State-machine log writer

```csharp
public interface IStateMachineLogWriter
{
    LogEntryWriter BeginEntry();
}
```

`BeginEntry` starts one entry for the state machine bound to that writer. Batch and snapshot writes use `StateMachineStorageWriter.BeginEntry()` while the manager already owns the write critical section.

### Entry writer

```csharp
public sealed class LogEntryWriter : IBufferWriter<byte>
{
    public Memory<byte> GetMemory(int sizeHint = 0);
    public Span<byte> GetSpan(int sizeHint = 0);
    public void Advance(int count);

    public void Write(ReadOnlySpan<byte> value);
    public void WriteVarUInt32(uint value);
    public void WriteVarUInt64(ulong value);

    internal void Commit();
    internal void Abort();
}
```

The writer is a reusable class-based `IBufferWriter<byte>` implementation, backed initially by the existing `ArcBufferWriter` infrastructure through `LogExtentBuffer`. It must not be allocated per log entry. The writer carries enough state to roll back the current entry and is reset before being reused. `RecyclableMemoryStream` or other backing stores can be considered later, but they are not required for the first implementation.

`BeginEntry` should preserve the current synchronous append critical section in `StateMachineManager.StateMachineLogWriter`: the manager lock is held while the entry is encoded, and internal `Commit` or `Abort` ends the writer lifetime and releases that critical section. Entry writing must remain synchronous; codecs must not retain the writer, returned `Memory<byte>`, or returned `Span<byte>` after the write call returns.

Using a real `IBufferWriter<byte>` writer keeps Orleans serialization, `Utf8JsonWriter`, MessagePack writers, protobuf writers, and custom value codecs on a familiar writer-side contract. The no-allocation requirement is satisfied by reusing writer instances rather than by making the writer stack-only.

### Entry reader

```csharp
public ref struct LogEntryReader
{
    public bool End { get; }
    public long Consumed { get; }
    public ReadOnlySequence<byte> Remaining { get; }
    public byte ReadByte();
    public uint ReadVarUInt32();
    public ulong ReadVarUInt64();
    public ReadOnlySequence<byte> ReadBytes(uint length);
    public void Skip(long length);
}
```

This wraps `SequenceReader<byte>` and hides format-specific safety checks behind durable-codec helpers. It should remain stack-only and should never produce a heap object for the logical entry itself.

Reader-side serializer interop should be built around `ReadOnlySequence<byte>` and serializer-native reader structs. For example, System.Text.Json can use `Utf8JsonReader`, Orleans serialization can use `Reader.Create(ReadOnlySequence<byte>, ...)`, and MessagePack can use `MessagePackReader`. Protocol Buffers value codecs should prefer parser or `CodedInputStream` paths which can consume the entry bytes without first allocating a new payload object where the library supports that.

### Durable list codec and state machine

```csharp
internal interface IDurableListCodec<T>
{
    void WriteAdd(T item, IBufferWriter<byte> output);
    void WriteSet(int index, T item, IBufferWriter<byte> output);
    void WriteInsert(int index, T item, IBufferWriter<byte> output);
    void WriteRemoveAt(int index, IBufferWriter<byte> output);
    void WriteClear(IBufferWriter<byte> output);
    void WriteSnapshot<TEnumerator>(ref TEnumerator items, int count, IBufferWriter<byte> output)
        where TEnumerator : struct, IEnumerator<T>;

    void Apply(ref LogEntryReader input, IDurableListStateMachine<T> stateMachine);
}

internal interface IDurableListStateMachine<T>
{
    void ApplyAdd(T item);
    void ApplySet(int index, T item);
    void ApplyInsert(int index, T item);
    void ApplyRemoveAt(int index);
    void ApplyClear();
    void ApplySnapshotStart(int count);
    void ApplySnapshotItem(T item);
}
```

The current `IDurableListLogEntryConsumer<T>` already has most of this shape. Rename it to emphasize that it is an internal state-machine apply interface, not a decoded-entry object visitor.

Apply the same pattern to dictionary, queue, set, value, persistent state, and task-completion-source codecs.

### Codec families and value codecs

`ILogDataCodec<T>` should be removed, but it should not be replaced by one universal `ILogValueCodec<T>`. Value codecs are format-specific members of a cohesive codec family. Selecting a journaling format selects the extent codec, durable operation codec providers, and value codec extension points together.

Do not add a public `IJournalingCodecFamily` abstraction initially. Treat codec families as a registration pattern: `UseJsonCodec`, `UseProtobufCodec`, `UseMessagePackCodec`, and the Orleans binary default each register a matched set of extent, durable operation, and value codecs. Add a formal abstraction only if implementation pressure proves that registration alone is insufficient.

Each family exposes its own public expert value-codec extension points as needed. For example:

1. JSON value codecs write JSON tokens using `Utf8JsonWriter` and read with `Utf8JsonReader`/source-generated metadata.
2. Protobuf value codecs own protobuf length concerns, such as `Measure`, field tags, and length-delimited value bodies.
3. Orleans binary value codecs use Orleans serialization readers/writers directly.
4. MessagePack value codecs can use `MessagePackWriter` and `MessagePackReader`.

This is an intentional API replacement, not a compatibility adapter. The current branch is still WIP, so keeping `ILogDataCodec<T>` would preserve a cross-format abstraction that cannot naturally express JSON token writing, protobuf length measurement, MessagePack shapes, and Orleans binary field writing without hidden buffering. Custom implementers own the allocation behavior of their format-specific codecs.

## Physical extent writer

`LogExtentBuilder` should be replaced or reshaped into a reusable `LogExtentBuffer`:

```csharp
internal sealed class LogExtentBuffer : IDisposable
{
    public long Length { get; }
    public bool IsEmpty { get; }

    public LogEntryWriter BeginEntry(StateMachineId streamId);
    public LogExtentReader CreateReader();
    public Stream AsReadOnlyStream();
    public void Reset();
}
```

The manager should keep one current `LogExtentBuffer` per activation and reuse it across append batches. Storage writes should return the buffer to the manager after the provider has consumed it.

### Format-specific extent framing

Log entry framing is format-specific:

1. JSON uses JSONL physical extents: each physical extent is written as one JSON array followed by a line feed, preserving the current "one extent per line" shape. JSONL is optimized for readability and interoperability, not the strict zero-allocation contract.
2. Orleans binary uses fixed32-framed log entries in the new versioned binary extent format.
3. Protocol Buffers uses fixed32-framed log entries for the physical extent, while protobuf remains the durable operation/value payload encoding inside each entry.
4. MessagePack uses fixed32-framed log entries for the physical extent, while MessagePack remains the durable operation/value payload encoding inside each entry.

This avoids forcing JSON into an artificial binary envelope while still giving the binary, protobuf, and MessagePack paths a backpatchable, allocation-free entry frame.

### Fixed32 binary/protobuf/MessagePack physical layout

The binary, protobuf, and MessagePack physical extent layout is a framed byte stream:

```text
extent      := entry*
entry       := length stream-id payload
length      := fixed32 little-endian byte length of (stream-id + payload)
stream-id   := varuint64
payload     := durable operation codec bytes
```

Why fixed32 length instead of varint length for binary/protobuf/MessagePack:

1. The writer can reserve 4 bytes, write the stream id and payload, then backpatch the length at commit.
2. No out-of-band `List<uint>` of entry lengths is needed.
3. No shifting is required when the actual varint width is known.
4. It is easy to validate during recovery.

JSONL does not use this length prefix. Its extent boundary is the storage block plus the terminating line feed, and its log entry boundaries are the elements in the JSON array.

### Commit and rollback

`BeginEntry` should capture the start offset, reserve the length prefix, write the stream id, and return an entry writer over the payload. `Commit` computes the entry length and backpatches the reserved prefix. `Abort` truncates the buffer to the captured start offset.

This requires the buffer to support:

1. Current absolute write offset.
2. Backpatching a small fixed-size region.
3. Truncating to a previous offset.
4. Reading the completed buffer as a sequence or stream.

If `ArcBufferWriter` cannot provide those operations cleanly, add an internal journaling-specific pooled segmented buffer rather than forcing every extent codec to work around missing primitives.

## Physical extent codec API

The existing API:

```csharp
byte[] Encode(LogExtentBuilder value);
LogExtent Decode(ArcBuffer value);
```

should be replaced with a streaming/visitor API:

```csharp
public interface IStateMachineLogExtentCodec
{
    IStateMachineLogExtentWriter CreateWriter(LogExtentBuffer buffer);
    void Read(ReadOnlySequence<byte> input, IStateMachineLogEntryConsumer consumer);
}

public interface IStateMachineLogEntryConsumer
{
    void OnEntry(StateMachineId streamId, ref LogEntryReader reader);
}
```

For storage providers which need a `Stream`, the extent writer should expose a read-only stream over the encoded bytes:

```csharp
public interface IStateMachineLogExtentWriter
{
    LogEntryWriter BeginEntry(StateMachineId streamId);
    Stream AsReadOnlyStream();
    ReadOnlySequence<byte> AsReadOnlySequence();
    void Reset();
}
```

The key rule is that extent codecs should write to caller-provided buffers or streams. They should not return `byte[]`.

## Storage API changes

`IStateMachineStorage.ReadAsync` currently returns `IAsyncEnumerable<LogExtent>`. That forces recovery to allocate extent objects and exposes entry enumeration as object-based `IEnumerable` in some cases.

Prefer a push-based recovery API:

```csharp
public interface IStateMachineStorage
{
    ValueTask ReadAsync(IStateMachineLogEntryConsumer consumer, CancellationToken cancellationToken);
    ValueTask AppendAsync(LogExtentBuffer value, CancellationToken cancellationToken);
    ValueTask ReplaceAsync(LogExtentBuffer value, CancellationToken cancellationToken);
    ValueTask DeleteAsync(CancellationToken cancellationToken);
    bool IsCompactionRequested { get; }
}
```

The manager implements `IStateMachineLogEntryConsumer` and dispatches directly to registered state machines. Storage providers can still read from Azure or memory asynchronously, but there is no per-entry `LogExtent.Entry` object and no decoded-entry list.

For providers which naturally read in blocks, `ReadAsync` can decode one physical extent at a time and call `consumer.OnEntry` for each entry before moving on.

## Format-specific notes

### Orleans binary

The binary codec should be the strict zero-allocation reference implementation.

1. Commands remain numeric tags.
2. Values are written directly through the Orleans binary family value codecs.
3. The new binary extent format uses fixed32 entry lengths and varuint64 stream ids.
4. The old binary extent format is not supported by the new reader; this WIP branch does not support mixed old/new extent recovery.

### Protocol Buffers

The protobuf codec needs the largest internal change because the current implementation creates intermediate byte arrays for value payloads.

Replace:

```csharp
byte[] ToBytes(T value);
T FromBytes(ReadOnlySequence<byte> bytes);
```

with:

```csharp
int Measure(T value);
void Write(T value, IBufferWriter<byte> writer);
T Read(ref LogEntryReader reader, int length);
```

For protobuf length-delimited fields:

1. If the value codec can compute size cheaply, write the length first and then the value.
2. If size is not known, use a measuring writer pass followed by the real write. This is CPU overhead but still avoids heap allocation.
3. Generated protobuf messages can use `CalculateSize()` for the length and `WriteTo(...)` for the body, provided we adapt the writer without allocating a `byte[]`.
4. Scalar values should write directly using the known protobuf wire encoding.

The physical protobuf extent should use the same fixed32-framed log entry envelope as the Orleans binary path. Protobuf remains the encoding for the durable operation payload and value payloads inside the entry. This is less protobuf-native than a fully length-delimited `LogExtent` message, but it avoids intermediate arrays and avoids a measuring pass for the physical extent envelope.

### MessagePack

MessagePack is in scope for this PR as a first-class codec family. Add an `Orleans.Journaling.MessagePack` package alongside JSON and protobuf.

The MessagePack family should:

1. Use the same fixed32-framed log entry envelope as Orleans binary and protobuf.
2. Encode durable operations and values using MessagePack inside each entry.
3. Use `MessagePackWriter` over the reusable `IBufferWriter<byte>` log entry writer.
4. Use closed generic MessagePack APIs and configured resolvers/options for value types.
5. Use `MessagePackReader` or closed generic `MessagePackSerializer.Deserialize<T>` over `ReadOnlySequence<byte>` on replay.
6. Avoid the generalized Orleans `MessagePackCodec` path when it would force runtime `Type` dispatch or reflection-heavy behavior that conflicts with AOT goals.
7. Throw helpful configuration errors when a value type is not supported by the configured MessagePack resolver/options.

### JSON / JSONL

JSON is the readable/debug/interchange format. It should reduce avoidable allocations where practical, but it is not held to the same strict zero **steady-state journaling overhead** contract as Orleans binary, protobuf-native, and MessagePack paths.

The JSON codec can still avoid the worst current allocations:

1. Keep JSONL as one physical extent per line, with each line containing a JSON array of log entries.
2. Do not parse an entry payload in the extent codec just to embed it in a JSON array. The extent writer should write the entry prefix (`{"streamId":8,"entry":`), let the durable codec write the entry JSON directly, and then write the suffix (`}`).
3. Do not use `JsonDocument` to dispatch commands. Use `Utf8JsonReader` directly and switch on UTF-8 property names and command values.
4. Use `JsonEncodedText` or UTF-8 literals for stable field names and command names.
5. Reuse or pool `Utf8JsonWriter` instances per extent writer if JSON remains on the hot path.

The JSON path should still avoid obvious unnecessary allocations, such as reparsing entry JSON in the extent codec or using `JsonDocument` for command dispatch when `Utf8JsonReader` is sufficient.

## Snapshot and batch writes

Snapshots should not call `BeginEntry` once per item unless the snapshot format intentionally stores each item as an independent command. Prefer one snapshot command per state machine:

```text
command: snapshot
count: N
items: item*
```

The state machine should use a batch writer:

```csharp
void IDurableStateMachine.AppendSnapshot(StateMachineStorageWriter snapshotWriter)
{
    var writer = snapshotWriter.BeginEntry();
    try
    {
        _codec.WriteSnapshot(_items, _items.Count, writer);
        writer.Commit();
    }
    catch
    {
        writer.Abort();
        throw;
    }
}
```

Do not add specialized batch durable operations initially. APIs such as `IDurableList<T>.AddRange` should write repeated existing entries, ideally under one extent/batch writer to avoid repeated lock acquisition. Set operations which mutate wholesale can continue to write a snapshot. Specialized batch commands can be added later if benchmarks or log-size pressure justify them.

## AOT compatibility

The design stays AOT-friendly by preserving closed generic codec resolution:

1. `DurableList<T>` stores `IDurableListCodec<T>`.
2. `DurableDictionary<TKey,TValue>` stores `IDurableDictionaryCodec<TKey,TValue>`.
3. Value codecs are format-specific closed generic services or source-generated serializer metadata, not `Type`-based runtime reflection.
4. Protobuf generated messages still require explicit parser/codec registration.
5. JSON still requires source-generated `JsonTypeInfo<T>` metadata.
6. MessagePack must use AOT-friendly resolver/options paths and avoid runtime `Type` dispatch on the hot path.

Do not add fallback paths based on `MakeGenericType`, parser property reflection, or `JsonSerializer.Serialize(object, Type, ...)`.

## Compatibility and migration

The current branch has multiple physical formats in flight:

1. Current Orleans binary extent format.
2. JSONL physical extents as one JSON array per line.
3. Protobuf physical extents as a stream of length-delimited `LogExtent` messages.
4. MessagePack physical extents are not implemented yet.

The zero-allocation writer produces a breaking physical format change for the binary/protobuf paths and introduces a new MessagePack physical format. Mixed old/new extent recovery is explicitly out of scope for this WIP PR. Tests should validate the new format and avoid carrying compatibility readers solely for intermediate branch data.

Recommended approach:

1. New writes use the new format for the selected codec.
2. New readers read the new format for the selected codec.
3. Existing persisted data from earlier WIP formats must be discarded or migrated out-of-band before using the new reader.
4. Future compatibility can be added later if this format ships publicly and needs to evolve.

## Error handling

Entry readers should validate aggressively:

1. Entry length cannot exceed remaining extent bytes.
2. Stream id must be present and valid.
3. Command tag must be present exactly once when the format requires it.
4. Required command arguments must be present.
5. Snapshot item count must match the number of items read.
6. Unknown commands should throw `NotSupportedException` with the durable collection type and command id/name.
7. Failed writes must abort the partially written entry before rethrowing.
8. Mutation methods must not update the in-memory data structure until the pending log entry has been encoded and committed to the current extent buffer.

Do not silently skip malformed entries. A malformed journal is a recovery failure.

## Validation strategy

Do not add exact allocation-count tests for this API shape. The intent is a low/no allocation design, but exact `GC.GetAllocatedBytesForCurrentThread()` assertions are brittle and are not required for PR acceptance.

Use normal functional tests plus targeted benchmarks and code review:

1. Functional tests should verify that each durable operation writes and replays correctly through the new entry reader/writer shape.
2. Storage-boundary tests should verify that append/replace no longer require `Encode(...): byte[]` and can consume a stream or buffer directly.
3. Benchmarks can measure allocation behavior for representative mutations and replay loops, but benchmark thresholds should not be required for correctness tests.
4. Code review should reject API paths that require command objects, delegate/tuple append plumbing, intermediate byte arrays, `JsonDocument` dispatch on hot paths, or materialized entry lists in recovery.

## Implementation plan

1. Add the new public reusable `LogEntryWriter : IBufferWriter<byte>` class, the public `LogEntryReader` ref struct, and the internal `LogExtentWriter` and `LogExtentReader` types alongside the current APIs.
2. Add allocation-free durable codec methods for list, dictionary, queue, set, value, state, and task-completion-source codecs.
3. Update `DurableList<T>` first as the proving ground: validate, write and commit the pending log entry, then apply the in-memory mutation; remove `AppendEntry(Action<TState,...>)` from `Add`, `set`, `Insert`, `RemoveAt`, `Clear`, and snapshot paths.
4. Update the binary codecs and binary/protobuf/MessagePack fixed32 extent writer to the new API.
5. Update the manager recovery loop to consume entries through a streaming reader instead of `IAsyncEnumerable<LogExtent>`.
6. Update storage providers so append/replace consume `LogExtentBuffer` or a read-only stream rather than `byte[]`.
7. Port dictionary, queue, set, value, state, and task-completion-source state machines.
8. Port protobuf value and entry codecs to direct write/read, removing `ToBytes` from hot paths.
9. Add the MessagePack journaling codec family, using fixed32-framed extents and MessagePack operation/value payloads.
10. Port JSON to streaming write/read where practical, with best-effort allocation reduction and no strict zero-allocation guarantee.
11. Remove intermediate compatibility readers for old WIP binary/protobuf extent formats; new JSON writes keep JSONL extents, and new binary/protobuf/MessagePack writes use fixed32-framed log entries.
12. Remove or obsolete the old delegate-based append API and `Encode(...): byte[]` API after all built-in codecs use the new path.

## Recommended first prototype

Prototype the binary path only:

1. Add `LogEntryWriter` and `LogEntryReader`.
2. Change `DurableList<T>.Add` to call `_codec.WriteAdd(item, writer)` directly before `ApplyAdd(item)`.
3. Implement the new methods on `OrleansBinaryListEntryCodec<T>`.
4. Use a fixed32-length framed `LogExtentBuffer`.
5. Add functional coverage for the new direct `DurableList<int>.Add` write/replay path, and use a benchmark if allocation measurement is needed.

That prototype will validate the core API and buffer mechanics before spending time on JSON/protobuf-specific details.
