---
layout: page
title: Included Log-Consistency Providers
---

# Built-In Log-Consistency Providers

The `Microsoft.Orleans.EventSourcing` package includes several log-consistency providers that cover basic scenarios suitable to get started, and allow some extensibility.


### Orleans.EventSourcing.**StateStorage**.LogConsistencyProvider

This provider stores *grain state snapshots*, using a standard storage provider that can be configured independently. 

The data that is kept in storage is an object that contains both the grain state (as specified by the first type parameter to `JournaledGrain`) and some meta-data (the version number, and a special tag that is used to avoid duplication of events when storage accesses fail).

Since the entire grain state is read/written every time we access storage, this provider is not suitable for objects whose grain state is very large.

This provider does not support `RetrieveConfirmedEvents`: it cannot retrieve the events from storage because the events are not persisted.

### Orleans.EventSourcing.**LogStorage**.LogConsistencyProvider

This provider stores *the complete event sequence as a single object*, using a standard storage provider that can be configured independently.

The data that is kept in storage is an object that contains a `List<EventType> object`, and some meta-data (a special tag that is used to avoid duplication of events when storage accesses fail).

This provider does support `RetrieveConfirmedEvents`. All events are always available and kept in memory.

Since the whole event sequence is read/written every time we access storage, this provider is _not suitable for use in production_, unless the event sequences are guaranteed to remain pretty short. The main purpose of this provider is to illustrate the semantics of the event sourcing, and for samples/testing environments.

### Orleans.EventSourcing.**CustomStorage**.LogConsistencyProvider

This provider allows the developer to plug in their own storage interface, which is then called by the conistency protocol at appropriate times. This provider does not make specific assumptions about whether what is stored are state snapshots or events - the programmer assumes control over that choice (and may store either or both).

To use this provider, a grain must derive from `JournaledGrain<StateType,EventType>`, as before, but additionally must also implement the following interface:

```csharp
public interface ICustomStorageInterface<StateType, EventType>
{
   Task<KeyValuePair<int,StateType>> ReadStateFromStorage();

   Task<bool> ApplyUpdatesToStorage(IReadOnlyList<EventType> updates, int expectedversion);
}
```
The consistency provider expects these to behave a certain way. Programmers should be aware that:

* The first method, `ReadStateFromStorage`, is expected to return both the version, and the state read. If there is nothing stored yet, it should return zero for the version and a state that matches corresponds to the default constructor for `StateType`.

* `ApplyUpdatesToStorage` must return false if the expected version does not match the actual version (this is analogous to an e-tag check). 

* If `ApplyUpdatesToStorage` fails with an exception, the consistency provider retries. This means some events could be duplicated if such an exception is thrown, but the event was actually persisted. The developer is responsible to make sure this is safe: e.g. either avoid this case by not throwing an exception, or ensure duplicated events are harmless for the application logic, or add some extra mechanism to filter duplicates.  

This provider does not support `RetrieveConfirmedEvents`. Of course, since the developer controls the storage interface anyway, they don't need to call this in the first place, but can implement their own event retrieval.

