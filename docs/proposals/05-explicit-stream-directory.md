# Proposal 5: Explicit log stream directory

## Status

Draft

## Problem

`LogManager` stores name-to-stream-id mappings using an internal `DurableDictionary<string, ulong>` subclass named `LogManagerState`. During recovery, stream id `0` entries are decoded as dictionary operations. When a `set` operation is applied, the dictionary calls back into `LogManager.OnSetLogStreamId`, which updates `_stateMachinesMap` and resets the target state machine.

Current control flow:

```text
stream 0 entry
  -> DurableDictionary.Apply
    -> ApplySet(name, id)
      -> OnSet(name, id)
        -> LogManager.OnSetLogStreamId
          -> _stateMachinesMap[id] = stateMachine
          -> stateMachine.Reset(writer)
```

This reuses durable dictionary machinery, but the manager metadata lifecycle is hidden behind a collection callback.

## Goals

- Make the stream directory role explicit.
- Preserve the persisted wire format.
- Make recovery easier to understand and evolve.
- Keep stream id routing owned by `LogManager`.

## Proposed design

Replace `LogManagerState : DurableDictionary<string, ulong>` with a dedicated internal state machine:

```csharp
private sealed class LogStreamDirectory :
    IDurableStateMachine,
    IDurableDictionaryOperationHandler<string, ulong>
{
    private readonly LogManager _manager;
    private readonly IDurableDictionaryOperationCodec<string, ulong> _codec;
    private readonly Dictionary<string, ulong> _ids = new(StringComparer.Ordinal);
    private ILogWriter? _storage;

    public void Reset(ILogWriter storage)
    {
        _ids.Clear();
        _storage = storage;
    }

    public void Apply(ReadOnlySequence<byte> entry)
    {
        _codec.Apply(entry, this);
    }

    public void ApplySet(string name, ulong id)
    {
        _ids[name] = id;
        _manager.BindStateMachine(name, id);
    }

    public void AppendSnapshot(LogWriter writer)
    {
        using var entry = writer.BeginEntry();
        _codec.WriteSnapshot(_ids, entry.Writer);
        entry.Commit();
    }
}
```

The directory can keep using the existing durable dictionary operation codec, so persisted bytes stay compatible.

## Required manager changes

Rename and clarify the callback:

```csharp
private void BindStateMachine(string name, ulong id)
{
    if (id >= _nextLogStreamId)
    {
        _nextLogStreamId = id + 1;
    }

    if (_stateMachines.TryGetValue(name, out var stateMachine))
    {
        _stateMachinesMap[id] = stateMachine;
        stateMachine.Reset(new ManagerLogWriter(this, new(id)));
    }
    else
    {
        var retired = new RetiredLogStream(new(id));
        _stateMachines.Add(name, retired);
        _stateMachinesMap[id] = retired;
    }
}
```

The manager should expose directory operations explicitly:

```csharp
_logStreamDirectory.TryGetValue(name, out var id);
_logStreamDirectory.Set(name, _nextLogStreamId++);
_logStreamDirectory.Remove(name);
```

## Benefits

- Recovery flow becomes more obvious.
- Reserved stream id `0` has a named, purpose-built implementation.
- Existing persisted data remains readable.
- Easier to combine with explicit recovery reset.

## Costs and risks

- Duplicates some `DurableDictionary` behavior.
- Must carefully preserve `OnSet` side effects, snapshot behavior, and remove behavior.
- Needs compatibility tests against existing binary, JSON, protobuf, and MessagePack data.

## Validation

- Existing recovery tests pass unchanged.
- Add tests proving old stream directory entries still recover after the implementation swap.
- Add tests for retired stream resurrection and permanent removal.
- Add tests covering delete-state reallocation of stream ids.

## Recommendation

Implement if recovery continues to evolve. This is primarily a clarity and maintainability improvement, and it pairs well with explicit recovery reset.
