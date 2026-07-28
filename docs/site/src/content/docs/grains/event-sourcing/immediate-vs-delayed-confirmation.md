---
title: Immediate and delayed confirmation
description: Learn the differences between immediate and delayed confirmation in .NET Orleans.
ms.date: 05/23/2025
ms.topic: concept-article
---

# Immediate and delayed confirmations

In this article, you learn the differences between immediate and delayed confirmations.

## Immediate confirmation

For many applications, you want to ensure events are confirmed immediately. This prevents the persisted version from lagging behind the current version in memory and avoids the risk of losing the latest state if the grain fails. You can guarantee this by following these rules:

1. Confirm all <xref:Orleans.EventSourcing.JournaledGrain`2.RaiseEvent*> calls using <xref:Orleans.EventSourcing.JournaledGrain`2.ConfirmEvents*> before the grain method returns.
1. Ensure tasks returned by <xref:Orleans.EventSourcing.JournaledGrain`2.RaiseConditionalEvent*> complete before the grain method returns.
1. Avoid <xref:Orleans.Concurrency.ReentrantAttribute> or <xref:Orleans.Concurrency.AlwaysInterleaveAttribute> attributes, so only one grain call processes at a time.

If you follow these rules, it means that after an event is raised, no other grain code can execute until the event has been written to storage. Therefore, it's impossible to observe inconsistencies between the in-memory version and the stored version. While this is often exactly what you want, it also has some potential disadvantages.

### Potential disadvantages

- If the connection to a remote cluster or storage is temporarily interrupted, the grain becomes unavailable. Effectively, the grain cannot execute any code while stuck waiting to confirm events, which can take an indefinite amount of time (the log-consistency protocol keeps retrying until storage connectivity is restored).

- When handling many updates to a single grain instance, confirming them one at a time can become very inefficient, potentially causing poor throughput.

## Delayed confirmation

To improve availability and throughput in the situations mentioned above, grains can choose to do one or both of the following:

- Allow grain methods to raise events without waiting for confirmation.
- Allow reentrancy, so the grain can keep processing new calls even if previous calls get stuck waiting for confirmation.

This means grain code can execute while some events are still being confirmed. The <xref:Orleans.EventSourcing.JournaledGrain`2> API has specific provisions giving you precise control over how to handle unconfirmed events currently _in flight_.

You can examine the following property to find out which events are currently unconfirmed:

```csharp
IEnumerable<EventType> UnconfirmedEvents { get; }
```

Also, since the state returned by the <xref:Orleans.EventSourcing.JournaledGrain`2.State?displayProperty=nameWithType> property doesn't include the effect of unconfirmed events, there's an alternative property:

```csharp
StateType TentativeState { get; }
```

This property returns a "tentative" state, obtained from <xref:Orleans.Grain`1.State> by applying all unconfirmed events. The tentative state is essentially a "best guess" at what will likely become the next confirmed state after all unconfirmed events are confirmed. However, there's no guarantee it actually will become the confirmed state. This is because the grain might fail, or the events might race against other events and lose, causing them to be canceled (if conditional) or appear later in the sequence than anticipated (if unconditional).

## Concurrency guarantees

Note that Orleans turn-based scheduling (cooperative concurrency) guarantees always apply, even when using reentrancy or delayed confirmation. This means that even though several methods might be in progress, only one can be actively executing—all others are stuck at an `await`. Therefore, there are never any true races caused by parallel threads.

In particular, note that:

- The properties <xref:Orleans.EventSourcing.JournaledGrain`2.State*>, <xref:Orleans.EventSourcing.JournaledGrain`2.TentativeState>, <xref:Orleans.EventSourcing.JournaledGrain`2.Version*>, and <xref:Orleans.EventSourcing.JournaledGrain`2.UnconfirmedEvents*> can change during the execution of a method.
- However, such changes can only happen while stuck at an `await`.

These guarantees assume your code stays within the [recommended practices](../external-tasks-and-grains.md) concerning tasks and async/await (in particular, doesn't use thread pool tasks, or only uses them for code that doesn't call grain functionality and that are properly awaited).
