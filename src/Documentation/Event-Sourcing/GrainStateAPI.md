---
layout: page
title: JournaledGrain API
---

# JournaledGrain Basics

Journaled grains derive from `JournaledGrain<StateType,EventType>`, with the following type parameters:

* The `StateType` represents the state of the grain. It must be a class with a public default constructor.  
* `EventType` is a common supertype for all the events that can be raised for this grain, and can be any class or interface. 

All state and event objects should be serializable (because the log-consistency providers may need to persist them, and/or send them in notification messages). 

For grains whose events are POCOs (plain old C# objects),  `JournaledGrain<StateType>` can be used as a shorthand for `JournaledGrain<StateType,Object>`.

## Reading the Grain State

To read the current grain state, and determine its version number, the JournaledGrain has properties

```csharp
GrainState State { get; }
int Version { get; }
```

The version number is always equal to the total number of confirmed events, and the state is the result of applying all the confirmed events to the initial state. The initial state, which has version 0 (because no events have been applied to it), is determined by the default constructor of the GrainState class.

_Important:_ The application should never directly modify the object returned by `State`. It is meant for reading only. Rather, when the application wants to modify the state, it must do so indirectly by raising events.

## Raising Events

Raising events is accomplished by calling the `RaiseEvent` function. For example, a grain representing a chat can raise a `PostedEvent` to indicate that a user submitted a post:

```csharp
RaiseEvent(new PostedEvent() { Guid = guid, User = user, Text = text, Timestamp = DateTime.UtcNow });
```

Note that `RaiseEvent` kicks off a write to storage access, but does not wait for the write to complete. For many applications, it is important to wait until we have confirmation that the event has been persisted. In that case, we always follow up by waiting for `ConfirmEvents`:

```csharp
RaiseEvent(new DepositTransaction() { DepositAmount = amount, Description = description });
await ConfirmEvents();
```

Note that even if you don't explicitly call `ConfirmEvents`, the events will eventually be confirmed - it happens automatically in the background. For more discussion on this topic, see [Immediate vs. Delayed Confirmation](MultiVersion.md).

## State Transition Methods

The runtime updates the grain state _automatically_ whenever events are raised. There is no need for the application to explicitly update the state after raising an event. However, the application still has to provide the code that specifies _how_ to update the state in response to an event. This can be done in two ways.

**(a)** The GrainState class can implement one or more `Apply` methods on the `StateType`. Typically, one would create multiple overloads, and the closest match is chosen for the runtime type of the event:
```csharp
class GrainState {
   
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

**(b)** The grain can override the TransitionState function:
```csharp
protected override void TransitionState(State state, EventType @event)
{
   // code that updates the state
}
```

The transition methods are assumed to have no side effects other than modifying the state object, and should be deterministic (otherwise, the effects are unpredictable).  If the transition code throws an exception, that exception is caught and included in a warning in the Orleans log, issued by the log-consistency provider.  

When, exactly, the runtime calls the transition methods depends on the chosen log consistency provider and its configuration. It is best for applications not to rely on a particular timing, except when specifically guaranteed by the log consistency provider. 

Some providers, such as the `LogStorage` log-consistency provider, replay the event sequence every time the grain is loaded. Therefore, as long as the event objects can still be properly deserialized from storage, it is possible to radically modify the GrainState class and the transition methods. But for other providers, such as the `StateStorage` log-consistency provider, only the `GrainState` object is persisted, so developers must ensure that it can be deserialized correctly when read from storage. 


## Raising Multiple Events

It is possible to make multiple calls to RaiseEvent before calling ConfirmEvents:

```csharp
RaiseEvent(e1);
RaiseEvent(e2);
await ConfirmEvents();
```

However, this is likely to cause two successive storage accesses, and it incurs a risk that the grain fails after writing only the first event. Thus, it is usually better to raise multiple events at once, using

```csharp
RaiseEvents(IEnumerable<EventType> events)
```

This guarantees that the given sequence of events is written to storage atomically. Note that since the version number always matches the length of the event sequence, raising multiple events increases the version number by more than one at a time.


## Retrieving the Event Sequence

The following method from the base `JournaledGrain` class allows the application to retrieve a specified segment of the sequence of all confirmed events:

```csharp
Task<IReadOnlyList<EventType>> RetrieveConfirmedEvents(int fromVersion, int toVersion)
```

However, it is not supported by all log consistency providers. If not supported, or if the specified segment of the sequence is no longer available, a `NotSupportedException` is thrown. 

To retrieve all events up to the latest confirmed version, one would call 
```csharp
await RetrieveConfirmedEvents(0, Version);
```

Only confirmed events can be retrieved: an exception is thrown if `toVersion` is larger than the current value of the property `Version`.

Since confirmed events never change, there are no races to worry about, even in the presence of [multiple instances](MultiInstance.md) or [delayed confirmation](MultiVersion.md). However, in such situations, it is possible that the value of the property `Version` is larger by the time the `await` resumes than at the time `RetrieveConfirmedEvents` is called, so it may be advisable to save its value in a variable. See also the section on [Concurrency Guarantees](MultiVersion.md).
 



