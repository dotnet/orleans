# Proposal 6: Explicit recovery reset

## Status

Draft

## Problem

`RecoverAsync` currently resets the current segment writer and the stream-id dictionary, then relies on replay of stream id `0` to rebuild application stream mappings and reset state machines. This works, but repeated recovery after a failure is difficult to reason about because some in-memory maps and retired placeholders may still contain data from a previous attempt.

## Goals

- Rebuild recovery state from first principles on each recovery pass.
- Make retry-after-failure behavior easier to reason about.
- Preserve retired stream buffering behavior.
- Avoid weakening write-path safety.

## Proposed design

Introduce an explicit recovery reset step:

```csharp
private void ResetForRecovery()
{
    _currentLogSegmentWriter?.Reset();

    _stateMachinesMap.Clear();
    _stateMachinesMap[LogStreamDirectory.Id] = _logStreamDirectory;
    _stateMachinesMap[RetiredLogStreamTracker.Id] = _retirementTracker;

    _nextLogStreamId = MinApplicationLogStreamId;

    _logStreamDirectory.ResetVolatileState();
    _retirementTracker.ResetVolatileState();

    foreach (var machine in _stateMachines.Values)
    {
        // Application machines are reset when their stream id is replayed.
        // Consider an explicit unbound state if needed.
    }
}
```

Call this at the start of `RecoverAsync` before reading storage:

```csharp
private async Task RecoverAsync(CancellationToken cancellationToken)
{
    lock (_lock)
    {
        ResetForRecovery();
    }

    using var recoveryBuffer = new ArcBufferWriter();
    await _storage.ReadAsync(recoveryBuffer, ProcessRecoveryBuffer, cancellationToken).ConfigureAwait(true);
    ProcessRecoveryBuffer(new ArcBufferReader(recoveryBuffer), isCompleted: true);

    CompleteRecovery();
}
```

## Open design question: unbound state machines

Current `IDurableStateMachine.Reset` requires an `ILogWriter`. During recovery, known application state machines only get a valid writer when their stream id is replayed. If we explicitly clear all mappings up front, then application machines need one of these behaviors:

1. remain untouched until bound by the directory,
2. be reset with a non-writing placeholder writer,
3. gain a separate internal recovery reset method.

Option 1 is least invasive. Proposal 5 makes it clearer because binding is handled by a named stream directory.

## Benefits

- Recovery retry behavior becomes deterministic and easier to inspect.
- `_stateMachinesMap` cannot accidentally retain stale stream bindings.
- Pairs naturally with explicit stream directory state.

## Costs and risks

- Needs careful handling for state machines registered before recovery but not present in the recovered stream directory.
- Retired placeholder preservation must be maintained.
- Could expose assumptions in current tests around late registration and delete-state behavior.

## Validation

- Add tests where recovery fails on malformed trailing data, then storage is fixed and recovery succeeds.
- Add tests for unknown stream preservation after recovery retry.
- Add tests for retired stream resurrection after recovery retry.
- Run all existing `LogManagerTests`.

## Recommendation

Do this after Proposal 5. Explicit reset is valuable, but it is safest once the stream directory is explicit and binding behavior is centralized.
