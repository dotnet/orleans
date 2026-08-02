---
title: Log-consistency providers
description: Compare built-in Orleans Event Sourcing log-consistency providers.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Log-consistency providers

`Microsoft.Orleans.EventSourcing` includes three providers:

| Provider | Durable representation | `RetrieveConfirmedEvents` | Scale consideration |
|---|---|---|---|
| State storage | Current state snapshot, version, metadata | No | Reads and writes the complete state record |
| Log storage | Complete event sequence and metadata in one record | Yes | Reads and writes the complete event sequence |
| Custom storage | Application-defined | No, through `JournaledGrain` | Determined by the implementation |

## State storage

<xref:Orleans.EventSourcing.StateStorage.LogConsistencyProvider> persists a snapshot using a configured <xref:Orleans.Storage.IGrainStorage>. It stores the state, confirmed version, and metadata used to avoid duplication after failures.

Use it when current state is the durable requirement and the complete event history isn't needed. Since every update writes the complete snapshot, large states increase serialization, transfer, and storage cost. Event retrieval isn't available because events aren't retained.

## Log storage

<xref:Orleans.EventSourcing.LogStorage.LogConsistencyProvider> persists the complete event sequence as one object using `IGrainStorage`. It keeps the complete sequence in memory and writes the complete sequence on updates.

It supports `RetrieveConfirmedEvents`, but its cost grows with the full history. Use it for samples, tests, or bounded logs. It isn't an append-optimized production event store and isn't suitable for an unbounded event sequence.

## Custom storage

<xref:Orleans.EventSourcing.CustomStorage.LogConsistencyProvider> calls storage methods implemented by the grain:

```csharp
public interface ICustomStorageInterface<TState, TEvent>
{
    Task<KeyValuePair<int, TState>> ReadStateFromStorage();

    Task<bool> ApplyUpdatesToStorage(
        IReadOnlyList<TEvent> updates,
        int expectedVersion);
}
```

`ReadStateFromStorage` returns the confirmed version and state. `ApplyUpdatesToStorage` must atomically compare `expectedVersion` and append/apply the supplied sequence. Return `false` on a version conflict.

The provider retries after exceptions. If storage committed but the response was lost, the same update can be submitted again. The implementation must make retries idempotent or detect duplicate submissions. Returning success before the update is durable violates `ConfirmEvents` semantics.

Use custom storage to integrate an append-optimized event store, snapshots plus events, retention, or application-specific migration. The implementation owns durability, concurrency, event retrieval, compaction, and schema evolution.
