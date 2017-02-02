---
layout: page
title: Immediate vs. Delayed Confirmation
---

# Immediate Confirmation

For many applications, we want to ensure that events are confirmed immediately, so that the persisted version does not lag behind the current version in memory, and we do not risk losing the latest state if the grain should fail. We can guarantee this by following these rules:

1. Confirm all `RaiseEvent` calls using `ConfirmEvents` before the grain method returns.

1. Make sure tasks returned by `RaiseConditionalEvent` complete before the grain method returns.

1. Avoid  `[Reentrant]` or `[AlwaysInterleave]` attributes, so only one grain call can be processed at a time.

If we follow these rules, it means that after an event is raised, no other grain code can execute until the event has been written to storage. Therefore, it is impossible to observe inconsistencies between the version in memory and the version in storage. While this is often exactly what we want, it also has some decided disadvantages.

* if the connection to a remote cluster or to storage is temporarily interrupted, then the grain becomes unavailable: effectively, the grain cannot execute any code while it is stuck waiting to confirm the events, which can take an indefinite amount of time (the confirmation protocol never times out).

* when handling a lot of of updates, confirming them one at a time can be very inefficient. 


# Delayed Confirmation

To improve availability and throughput, grains can choose to do one or both of the following:

* allow grain methods to raise events without waiting for confirmation. 

* allow reentrancy, so the grain can keep processing new calls even if previous calls get stuck waiting for confirmation.

This means it is possible for grain code to execute while some events are still in the process of being confirmed. The `JournaledGrain` API has some specific provisions to give developers precise control over how to deal with unconfirmed events that are currently "in flight".

The following property can be examined to find out what events are currently unconfirmed:

```csharp
IEnumerable<EventType> UnconfirmedEvents { get; }
```
Also, since the state returned by the `State` property does not include the effect of unconfirmed events, there is an alternative property 

```csharp
StateType TentativeState { get; }
```

which returns a "tentative" state, obtained from "State" by applying all the unconfirmed events. The tentative state is essentially a "best guess" at what will likely become the next confirmed state, after all unconfirmed events are confirmed. However, there is no guarantee that it actually will, because the grain may fail, or because the events may race against other events and lose, causing them to be canceled (if they are conditional) or appear at a later position in the sequence than anticipated (if they are unconditional). 

Just like reacting to state changes via `OnStateChanged`, grains can react to changes in the tentative state:

```csharp
protected override void OnTentativeStateChanged()
{
   // read state and/or events and take appropriate action
}
```

`OnTentativeStateChanged` is called whenever the tentative state changes, i.e. if the combined sequence  (ConfirmedEvents + UnconfirmedEvents) changes.

## Concurrency

Note that even with delayed confirmation, Orleans turn-based scheduling (cooperative concurrency) always applies: several grain methods may be in progress, but only one can be actively executing, i.e. not stuck at an await. In particular, even though the variables `State`, `TentativeState`, `Version`, and `UnconfirmedEvents` can change during the execution of a grain method, this can only happen while stuck at an await.

