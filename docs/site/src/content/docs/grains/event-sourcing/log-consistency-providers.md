---
title: Log-consistency providers
description: Learn about log-consistency providers in .NET Orleans.
ms.date: 03/29/2025
ms.topic: concept-article
---

# Log-consistency providers

The `Microsoft.Orleans.EventSourcing` package includes several log-consistency providers covering basic scenarios suitable for getting started and allowing some extensibility.

### State storage

The <xref:Orleans.EventSourcing.StateStorage.LogConsistencyProvider?displayProperty=nameWithType> stores *grain state snapshots* using a standard storage provider that you can configure independently.

The data kept in storage is an object containing both the grain state (specified by the first type parameter to `JournaledGrain`) and some metadata (the version number and a special tag used to avoid event duplication when storage accesses fail).

Since the entire grain state is read/written every time storage is accessed, this provider isn't suitable for objects with very large grain states.

This provider doesn't support <xref:Orleans.EventSourcing.JournaledGrain`2.RetrieveConfirmedEvents*>. It cannot retrieve events from storage because the events aren't persisted.

### Log storage

The <xref:Orleans.EventSourcing.LogStorage.LogConsistencyProvider?displayProperty=nameWithType> stores *the complete event sequence as a single object* using a standard storage provider that you can configure independently.

The data kept in storage is an object containing a `List<EventType>` object and some metadata (a special tag used to avoid event duplication when storage accesses fail).

This provider supports <xref:Orleans.EventSourcing.JournaledGrain`2.RetrieveConfirmedEvents*>. All events are always available and kept in memory.

Since the entire event sequence is read/written every time storage is accessed, this provider is *not suitable for production use* unless the event sequences are guaranteed to remain fairly short. The main purpose of this provider is to illustrate the semantics of event sourcing and for use in samples/testing environments.

### Custom storage

This <xref:Orleans.EventSourcing.CustomStorage.LogConsistencyProvider?displayProperty=nameWithType> allows you to plug in your storage interface, which the consistency protocol then calls at appropriate times. This provider doesn't make specific assumptions about whether the stored data consists of state snapshots or events – you assume control over that choice (and can store either or both).

To use this provider, a grain must derive from <xref:Orleans.EventSourcing.JournaledGrain`2>, as before, but must also implement the following interface:

```csharp
public interface ICustomStorageInterface<StateType, EventType>
{
    Task<KeyValuePair<int, StateType>> ReadStateFromStorage();

    Task<bool> ApplyUpdatesToStorage(
        IReadOnlyList<EventType> updates,
        int expectedVersion);
}
```

The consistency provider expects these methods to behave in a certain way. Be aware that:

- The first method, <xref:Orleans.EventSourcing.CustomStorage.ICustomStorageInterface`2.ReadStateFromStorage*>, is expected to return both the version and the state read. If nothing is stored yet, it should return zero for the version and a state corresponding to the default constructor for `StateType`.

- <xref:Orleans.EventSourcing.CustomStorage.ICustomStorageInterface`2.ApplyUpdatesToStorage*> must return `false` if the expected version doesn't match the actual version (this is analogous to an e-tag check).

- If `ApplyUpdatesToStorage` fails with an exception, the consistency provider retries. This means some events could be duplicated if such an exception is thrown but the event was persisted. You are responsible for ensuring this is safe: for example,, either avoid this case by not throwing an exception, ensure duplicated events are harmless for the application logic, or add an extra mechanism to filter duplicates.

This provider doesn't support `RetrieveConfirmedEvents`. Of course, since you control the storage interface anyway, you don't need to call this method in the first place but can implement your event retrieval logic.
