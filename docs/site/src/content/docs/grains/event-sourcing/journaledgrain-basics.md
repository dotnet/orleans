---
title: The JournaledGrain API
description: Learn the concepts of the JournaledGrain API in .NET Orleans.
ms.date: 05/23/2025
ms.topic: concept-article
---

# JournaledGrain basics

Journaled grains derive from <xref:Orleans.EventSourcing.JournaledGrain`2>, with the following type parameters:

- `TGrainState` represents the state of the grain. It must be a class with a public default constructor.
- `TEventBase` is a common supertype for all events that can be raised for this grain and can be any class or interface.

All state and event objects should be serializable because log-consistency providers might need to persist them and/or send them in notification messages.

For grains whose events are POCOs (plain old C# objects), you can use <xref:Orleans.EventSourcing.JournaledGrain`1> as a shorthand for <xref:Orleans.EventSourcing.JournaledGrain`2>.

## Reading the grain state

To read the current grain state and determine its version number, `JournaledGrain` has these properties:

```csharp
GrainState State { get; }
int Version { get; }
```

The version number always equals the total number of confirmed events, and the state is the result of applying all confirmed events to the initial state. The default constructor of the `GrainState` class determines the initial state, which has version 0 (because no events have been applied to it).

> [!IMPORTANT]
> Never directly modify the object returned by <xref:Orleans.EventSourcing.JournaledGrain`2.State>. It's meant for reading only. When your application needs to modify the state, do so indirectly by raising events.

## Raise events

Raise events by calling the <xref:Orleans.EventSourcing.JournaledGrain`2.RaiseEvent*> function. For example, a grain representing a chat can raise a `PostedEvent` to indicate that a user submitted a post:

```csharp
RaiseEvent(new PostedEvent()
{
    Guid = guid,
    User = user,
    Text = text,
    Timestamp = DateTime.UtcNow
});
```

Note that <xref:Orleans.EventSourcing.JournaledGrain`2.RaiseEvent*> initiates a write to storage but doesn't wait for the write to complete. For many applications, it's important to wait for confirmation that the event has been persisted. In that case, always follow up by waiting for <xref:Orleans.EventSourcing.JournaledGrain`2.ConfirmEvents*>:

```csharp
RaiseEvent(new DepositTransaction()
{
    DepositAmount = amount,
    Description = description
});
await ConfirmEvents();
```

Note that even if you don't explicitly call <xref:Orleans.EventSourcing.JournaledGrain`2.ConfirmEvents*>, the events eventually get confirmed automatically in the background.

## State transition methods

The runtime updates the grain state _automatically_ whenever events are raised. Your application doesn't need to explicitly update the state after raising an event. However, your application still needs to provide the code specifying _how_ to update the state in response to an event. You can do this in two ways:

**(a)** The `GrainState` class can implement one or more `Apply` methods on the `StateType`. Typically, you create multiple overloads, and the runtime chooses the closest match for the runtime type of the event:

```csharp
class GrainState
{
    Apply(E1 @event)
    {
        // code that updates the state
    }

    Apply(E2 @event)
    {
        // code that updates the state
    }
}
```

**(b)** The grain can override the `TransitionState` function:

```csharp
protected override void TransitionState(
    State state, EventType @event)
{
   // code that updates the state
}
```

Assume transition methods have no side effects other than modifying the state object and should be deterministic (otherwise, the effects are unpredictable). If the transition code throws an exception, Orleans catches it and includes it in a warning in the Orleans log, issued by the log-consistency provider.

When exactly the runtime calls the transition methods depends on the chosen log-consistency provider and its configuration. Applications shouldn't rely on specific timing unless the log-consistency provider explicitly guarantees it.

Some providers, like the <xref:Orleans.EventSourcing.LogStorage> log-consistency provider, replay the event sequence every time the grain loads. Therefore, as long as the event objects can still be properly deserialized from storage, you can radically modify the `GrainState` class and the transition methods. However, for other providers, such as the <xref:Orleans.EventSourcing.StateStorage> log-consistency provider, only the `GrainState` object is persisted. In this case, you must ensure it can be deserialized correctly when read from storage.

## Raise multiple events

You can make multiple calls to <xref:Orleans.EventSourcing.JournaledGrain`2.RaiseEvent*> before calling <xref:Orleans.EventSourcing.JournaledGrain`2.ConfirmEvents*>:

```csharp
RaiseEvent(e1);
RaiseEvent(e2);
await ConfirmEvents();
```

However, this likely causes two successive storage accesses and incurs a risk that the grain fails after writing only the first event. Thus, it's usually better to raise multiple events at once using:

```csharp
RaiseEvents(IEnumerable<EventType> events)
```

This guarantees the given sequence of events is written to storage atomically. Note that since the version number always matches the length of the event sequence, raising multiple events increases the version number by more than one at a time.

## Retrieve the event sequence

The following method from the base `JournaledGrain` class allows your application to retrieve a specified segment of the sequence of all confirmed events:

```csharp
Task<IReadOnlyList<EventType>> RetrieveConfirmedEvents(
    int fromVersion,
    int toVersion);
```

However, not all log-consistency providers support this method. If it's not supported, or if the specified segment of the sequence is no longer available, a <xref:System.NotSupportedException> is thrown.

To retrieve all events up to the latest confirmed version, call:

```csharp
await RetrieveConfirmedEvents(0, Version);
```

You can only retrieve confirmed events: an exception is thrown if `toVersion` is larger than the current value of the `Version` property.

Since confirmed events never change, there are no races to worry about, even with multiple instances or delayed confirmation. However, in such situations, the value of the `Version` property might be larger by the time the `await` resumes than when `RetrieveConfirmedEvents` was called. Therefore, it might be advisable to save its value in a variable. See also the section on [Concurrency Guarantees](immediate-vs-delayed-confirmation.md#concurrency-guarantees).
