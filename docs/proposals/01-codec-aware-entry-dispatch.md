# Proposal 1: Codec-owned recovery dispatch

## Status

Draft

## Problem

Recovery currently passes every decoded log entry through a raw byte callback:

```text
ILogStorage.ReadAsync
  -> LogManager.ProcessRecoveryBuffer
    -> ILogFormat.TryRead(reader, ILogEntrySink, isCompleted)
      -> ILogEntrySink.OnEntry(LogStreamId, ReadOnlySequence<byte>)
        -> LogManager finds IDurableStateMachine
          -> IDurableStateMachine.Apply(ReadOnlySequence<byte>)
            -> durable operation codec parses payload bytes
              -> durable operation handler mutates state
```

This keeps physical framing separate from durable operation decoding, but it also forces the physical format to throw away useful parsing work. JSON is the clearest example: `JsonLinesLogFormat` parses each JSON line to validate the outer frame and slice out the `entry` payload, then the durable JSON operation codec parses that same payload again.

The current control flow also puts too much behavior behind `ILogEntrySink.OnEntry`: it both resolves the stream id and applies the payload. That makes it hard for an `ILogFormat` implementation to collaborate with the codec which actually knows how to decode the payload.

## Goals

- Let `ILogFormat` resolve the target state machine and then dispatch through that state machine's codec.
- Let a format-specific codec consume a format-specific parsed payload, such as `JsonElement`, without reparsing raw bytes.
- Give `ILogFormat` only non-generic contracts to check: the resolved state machine accepts this format's typed recovery path, and its codec can apply this format's typed payload.
- Keep `LogManager` responsible for stream-id routing, not durable operation decoding.
- Treat codec/format mismatches as errors for active state machines instead of silently falling back to raw bytes.
- Buffer unknown or retired streams as formatted entries, since those entries must be copied forward during compaction without being interpreted by their original state machine.
- Avoid making storage format-aware.

## Non-goals

- Do not merge physical log framing and durable operation codecs into one monolithic codec.
- Do not make `LogManager` depend on JSON, MessagePack, Protobuf, or any other concrete format.
- Do not require every physical format to implement typed recovery dispatch immediately.
- Do not remove the existing `ReadOnlySequence<byte>` codec path for formats where the raw payload is already the most efficient representation.

## Proposed design

Split stream routing from entry application. Replace `ILogEntrySink` with an interface whose job is only to resolve a `LogStreamId` to its durable state machine.

Use `ILogStreamStateMachineResolver` as the proposed name. It is more specific than `IStateMachineProvider`: the interface resolves log stream ids during log replay, and it may create a retired-stream placeholder when the stream id is unknown.

```csharp
public interface ILogStreamStateMachineResolver
{
    IDurableStateMachine ResolveStateMachine(LogStreamId streamId);
}
```

`ILogFormat.TryRead` then receives the resolver instead of the old push-style sink:

```csharp
public interface ILogFormat
{
    ILogSegmentWriter CreateWriter();
    bool TryRead(
        ArcBufferReader input,
        ILogStreamStateMachineResolver resolver,
        bool isCompleted);
}
```

`LogManager` remains the only component which knows how stream ids map to durable state machines:

```csharp
IDurableStateMachine ILogStreamStateMachineResolver.ResolveStateMachine(LogStreamId streamId)
{
    if (!_stateMachinesMap.TryGetValue(streamId.Value, out var stateMachine))
    {
        stateMachine = new RetiredLogStream(streamId);
        _stateMachinesMap[streamId.Value] = stateMachine;
    }

    return stateMachine;
}
```

The default raw-byte flow moves into the physical format:

```csharp
var stateMachine = resolver.ResolveStateMachine(streamId);
stateMachine.Apply(payload);
```

This keeps `OrleansBinaryLogFormat`, and any format which does not have a typed recovery representation, simple. The important change is that formats which do have a typed representation can ask the resolver for the state machine, cast that state machine and its codec to format-specific non-generic interfaces, and then dispatch through the codec.

Expose the durable operation codec from `IDurableStateMachine`:

```csharp
public interface IDurableStateMachine
{
    object OperationCodec { get; }

    void Reset(ILogWriter storage);
    void Apply(ReadOnlySequence<byte> entry);
    void OnRecoveryCompleted() { }
    void AppendEntries(LogWriter writer);
    void AppendSnapshot(LogWriter writer);
    void OnWriteCompleted() { }
    IDurableStateMachine DeepCopy();
}
```

Each built-in state machine returns the codec it already stores:

```csharp
object IDurableStateMachine.OperationCodec => _codec;
```

If adding a public `object` property to `IDurableStateMachine` is too much public API surface, use an internal companion interface instead:

```csharp
internal interface IHasDurableOperationCodec
{
    object OperationCodec { get; }
}
```

The rest of the design is the same. The key requirement is that a physical format can get the codec instance associated with the resolved state machine.

## Retired stream formatted entries

`RetiredLogStream` should not need to know the representation used by a log format. It can be a holder for opaque formatted entries supplied by the active `ILogFormat`:

```csharp
internal interface IFormattedLogEntryBuffer
{
    void AddFormattedEntry(object entry);
    IReadOnlyList<object> FormattedEntries { get; }
}
```

`RetiredLogStream` implements the holder interface and does not interpret the entries:

```csharp
private sealed class RetiredLogStream(LogStreamId streamId)
    : IDurableStateMachine, IFormattedLogEntryBuffer
{
    private readonly List<object> _formattedEntries = [];

    public LogStreamId StreamId { get; } = streamId;
    public IReadOnlyList<object> FormattedEntries => _formattedEntries;

    public void AddFormattedEntry(object entry) => _formattedEntries.Add(entry);

    void IDurableStateMachine.AppendSnapshot(LogWriter snapshotWriter)
    {
        foreach (var entry in _formattedEntries)
        {
            snapshotWriter.AppendFormattedEntry(entry);
        }
    }
}
```

When compaction writes retired streams back out, `LogWriter` forwards the opaque entry to the format-owned writer:

```csharp
internal void AppendFormattedEntry(object entry)
{
    GetTarget().AppendFormattedEntry(_id, entry);
}
```

The format-owned writer validates the entry type before writing it. The object travels from the format reader to `RetiredLogStream` and back to the same format family's writer without being interpreted by `LogManager`:

```csharp
void JsonLinesLogSegmentWriter.AppendFormattedEntry(LogStreamId streamId, object entry)
{
    if (entry is not JsonFormattedLogEntry jsonEntry)
    {
        throw new InvalidOperationException(
            $"JSON log writer cannot append formatted entry of type '{entry.GetType().FullName}'.");
    }

    WriteJsonEntry(streamId, jsonEntry);
}
```

This keeps the retired-stream state machine independent of `ReadOnlySequence<byte>`. Orleans binary may use raw payload bytes because that is its natural formatted representation. JSON could keep the entry as raw UTF-8 bytes, a cloned `JsonElement`, or a small JSON-specific holder containing the already-sliced entry payload. MessagePack and Protobuf can choose representations which are efficient for their own writers.

## JSON typed dispatch

Add JSON-specific non-generic recovery contracts. The state-machine target contract must live somewhere the built-in state machines can implement and the JSON format can see, such as `Orleans.Journaling` or a shared abstractions assembly:

```csharp
public interface IJsonLogEntrySink
{
}
```

The codec contract can remain internal to the JSON package, since both `JsonLinesLogFormat` and the JSON operation codecs live there:

```csharp
internal interface IJsonLogEntryCodec
{
    void Apply(JsonElement entry, IJsonLogEntrySink sink);
}
```

`IJsonLogEntrySink` is the format-specific target contract which `ILogFormat` needs to know about. It does not expose closed generic types. Built-in durable state machines implement it as part of the refactor, along with their existing operation handler interfaces.

If we want to avoid a public `IJsonLogEntrySink`, make it internal in `Orleans.Journaling` and expose it to the JSON package using `InternalsVisibleTo`, or keep it public and document it as an advanced recovery extension point. The important point is that the target contract is shared by the core state-machine implementations and the format package:

```csharp
internal interface IJsonLogEntrySink
{
}
```

The important point is the same: `JsonLinesLogFormat` checks only a non-generic state-machine target contract and a non-generic codec contract. It does not need to know `IDurableDictionaryOperationHandler<TKey, TValue>`, `IDurableListOperationHandler<T>`, or any other closed generic handler type.

`JsonLinesLogFormat` parses the line once, resolves the state machine, checks the codec, and invokes the typed path:

```csharp
using var document = JsonDocument.Parse(line);
var root = document.RootElement;
var streamId = new LogStreamId(root.GetProperty("streamId").GetUInt64());
var entry = root.GetProperty("entry");

var stateMachine = resolver.ResolveStateMachine(streamId);
if (stateMachine is IFormattedLogEntryBuffer formattedTarget)
{
    formattedTarget.AddFormattedEntry(JsonFormattedLogEntry.From(entry));
    return;
}

if (stateMachine is not IJsonLogEntrySink jsonSink)
{
    throw new InvalidOperationException(
        $"The JSON log entry for stream {streamId.Value} resolved to state machine " +
        $"'{stateMachine.GetType().FullName}', which does not implement IJsonLogEntrySink.");
}

if (stateMachine.OperationCodec is not IJsonLogEntryCodec jsonCodec)
{
    throw new InvalidOperationException(
        $"The JSON log entry for stream {streamId.Value} resolved to state machine " +
        $"'{stateMachine.GetType().FullName}', but its codec " +
        $"'{stateMachine.OperationCodec.GetType().FullName}' does not implement IJsonLogEntryCodec.");
}

jsonCodec.Apply(entry, jsonSink);
```

The `IFormattedLogEntryBuffer` branch is not a codec mismatch fallback. It is an explicit formatted-entry path for unknown or retired streams. Active state machines must have a codec compatible with the active physical log format. If they do not, recovery fails.

Each built-in state machine implements the JSON target contract as part of the refactor:

```csharp
internal class DurableDictionary<K, V>
    : IDurableStateMachine,
      IJsonLogEntrySink,
      IDurableDictionaryOperationHandler<K, V>
    where K : notnull
{
    object IDurableStateMachine.OperationCodec => _codec;
}
```

Each JSON durable operation codec implements `IJsonLogEntryCodec` and delegates to a typed `JsonElement` overload:

```csharp
public sealed class JsonDictionaryOperationCodec<TKey, TValue>
    : IDurableDictionaryOperationCodec<TKey, TValue>, IJsonLogEntryCodec
    where TKey : notnull
{
    public void Apply(
        ReadOnlySequence<byte> input,
        IDurableDictionaryOperationHandler<TKey, TValue> consumer)
    {
        var reader = new Utf8JsonReader(input);
        using var document = JsonDocument.ParseValue(ref reader);
        Apply(document.RootElement, consumer);
    }

    internal void Apply(
        JsonElement entry,
        IDurableDictionaryOperationHandler<TKey, TValue> consumer)
    {
        var command = entry.GetProperty(JsonLogEntryFields.Command).GetString();
        switch (command)
        {
            case JsonLogEntryCommands.Set:
                consumer.ApplySet(
                    _keySerializer.Deserialize(entry.GetProperty(JsonLogEntryFields.Key))!,
                    _valueSerializer.Deserialize(entry.GetProperty(JsonLogEntryFields.Value))!);
                break;

            // Other commands omitted.
        }
    }

    void IJsonLogEntryCodec.Apply(JsonElement entry, IJsonLogEntrySink sink)
    {
        if (sink is not IDurableDictionaryOperationHandler<TKey, TValue> consumer)
        {
            throw new InvalidOperationException(
                $"Operation sink '{sink.GetType().FullName}' is not compatible with codec " +
                $"'{GetType().FullName}'.");
        }

        Apply(entry, consumer);
    }
}
```

This cast is codec-local validation of the state-machine/codec pairing. `JsonLinesLogFormat` only sees `IJsonLogEntrySink` and `IJsonLogEntryCodec`, so it does not need to know whether a stream is a dictionary, list, queue, set, value, persistent state, or task completion source.

The same pattern can be repeated by `JsonListOperationCodec<T>`, `JsonQueueOperationCodec<T>`, `JsonSetOperationCodec<T>`, `JsonValueOperationCodec<T>`, `JsonStateOperationCodec<T>`, and `JsonTcsOperationCodec<T>`.

## JsonElement lifetime

The `JsonElement` is callback-scoped. `JsonLinesLogFormat` owns the `JsonDocument`, invokes `jsonCodec.Apply(...)` synchronously, and disposes the document immediately after the call returns.

Codec implementations must not retain the `JsonElement` or any child elements. They can deserialize values, apply operations to the handler, and then return.

Retired streams continue to use the format-owned formatted entry. If that entry depends on `JsonElement`, it must clone any data it needs before the `JsonDocument` is disposed. That keeps compaction behavior independent from the `JsonDocument` lifetime.

## Why throw instead of fallback?

The log format key is already scoped per grain and is used to resolve both the physical `ILogFormat` and the durable operation codec providers. If the configured key is `json`, active state machines should be using JSON codecs. If a resolved active state machine has a non-JSON codec, falling back to `stateMachine.Apply(...)` with decoded payload bytes hides a configuration bug and may reintroduce the double-parse path unpredictably.

For active state machines:

```text
physical format = json
state machine implements IJsonLogEntrySink
operation codec implements IJsonLogEntryCodec -> apply typed payload
state machine or codec does not implement the JSON typed contract -> throw
```

For retired or unknown streams:

```text
state machine is formatted-entry target -> store format-owned formatted entry
```

That distinction keeps recovery strict for live state while preserving the existing ability to carry forward entries for state machines which are no longer registered.

## Impact on other formats

`OrleansBinaryLogFormat` can continue to resolve the state machine and call `Apply(ReadOnlySequence<byte>)` because the binary payload is the codec's native input.

Protobuf and MessagePack can adopt the same pattern later if it produces measurable wins:

```csharp
internal interface IProtobufLogEntryCodec
{
    void Apply(ReadOnlySequence<byte> payload, IProtobufLogEntrySink sink);
}

internal interface IMessagePackLogEntryCodec
{
    void Apply(ref MessagePackReader reader, IMessagePackLogEntrySink sink);
}
```

The exact typed payload should be chosen per format. JSON benefits from `JsonElement` because the outer JSON Lines frame and the durable operation payload are part of the same parsed document. MessagePack may benefit from passing a positioned `MessagePackReader`. Protobuf may benefit less unless the physical frame reader can avoid copying or can expose a `CodedInputStream`/reader over the payload.

## Benefits

- JSON recovery parses each JSON line once instead of parsing the outer frame and then reparsing the `entry` payload.
- `LogManager` becomes simpler: it resolves stream ids but does not participate in format-specific application.
- Format/codec compatibility is explicit and fail-fast.
- `ILogFormat` only checks non-generic target and codec contracts. Closed generic handler details stay inside the state-machine/codec pair.
- Retired stream formatting remains explicit while allowing each format to choose its own formatted entry representation.

## Costs and risks

- `ILogEntrySink` is replaced by `ILogStreamStateMachineResolver`, so every `ILogFormat` implementation must be updated.
- Exposing `OperationCodec` on `IDurableStateMachine` is a public API change unless an internal companion interface is used.
- The JSON typed target contract must live somewhere the built-in state machines can implement it and the JSON format can see it, either as public API or as an internal shared contract exposed to the JSON assembly.
- The JSON package gains a non-generic codec contract and a small amount of codec-local casting/diagnostic code.
- Formatted retired entries become opaque objects, so each format writer must fail fast when asked to write an entry object from another format.
- `JsonElement` lifetime rules must be documented and tested.
- `JsonDocument.Parse(line)` parses the whole line and allocates a document, so the performance win should be measured against the current `Utf8JsonReader` slicing plus second `JsonDocument.ParseValue` path.

## Validation

- Existing JSON, protobuf, MessagePack, and Orleans binary recovery tests continue to pass.
- Add tests proving JSON recovery calls `IJsonLogEntryCodec` for active state machines.
- Add tests proving JSON recovery validates that the state machine implements the JSON target contract.
- Add tests proving JSON recovery throws when an active state machine's codec does not implement `IJsonLogEntryCodec`.
- Add tests proving retired and unknown streams still buffer format-owned entries and compact them forward.
- Add tests proving each format writer rejects formatted entries from another format.
- Add tests covering `JsonElement` lifetime by ensuring codecs do not retain elements past the apply call.
- Add a benchmark comparing current JSON recovery to single-parse `JsonElement` recovery.

## Recommendation

Adopt the resolver-plus-codec-dispatch design for JSON first. Keep raw byte application for formats where raw bytes are the native codec input, and use format-owned formatted entries for retired streams. Do not use raw byte application as a fallback when an active state machine has the wrong codec for the active physical format.
