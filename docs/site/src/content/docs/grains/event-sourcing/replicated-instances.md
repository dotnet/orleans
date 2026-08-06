---
title: Replicated grains
description: Learn the concepts of replicated grains in .NET Orleans.
ms.date: 05/23/2025
ms.topic: concept-article
---

# Replicated grains

Multiple instances of the same grain can be active in certain scenarios. `JournaledGrain` is designed to support replicated instances with minimal friction. It relies on *log-consistency providers* to run the necessary protocols ensuring all instances agree on the same sequence of events. In particular, it handles the following aspects:

- **Consistent versions**: All versions of the grain state (except tentative versions) are based on the same global sequence of events. In particular, if two instances see the same version number, they see the same state.

- **Racing events**: Multiple instances can simultaneously raise an event. The consistency provider resolves this race and ensures all instances agree on the same sequence.

- **Notifications/Reactivity**: After an event is raised at one grain instance, the consistency provider not only updates storage but also notifies all other grain instances.

For a general discussion of the consistency model, see our [TechReport](https://www.microsoft.com/research/publication/geo-distribution-actor-based-services/) and the [GSP paper](https://www.microsoft.com/research/publication/global-sequence-protocol-a-robust-abstraction-for-replicated-shared-state-extended-version/) (Global Sequence Protocol).

## Conditional events

Racing events can be problematic if they conflict, i.e., both shouldn't commit for some reason. For example, when withdrawing money from a bank account, two instances might independently determine sufficient funds exist for a withdrawal and issue a withdrawal event. However, the combination of both events could overdraw the account. To avoid this, the `JournaledGrain` API supports the <xref:Orleans.EventSourcing.JournaledGrain`2.RaiseConditionalEvent*> method.

```csharp
bool success = await RaiseConditionalEvent(
    new WithdrawalEvent() { /* ... */ });
```

Conditional events double-check if the local version matches the version in storage. If not, it means the event sequence has grown in the meantime, indicating this event lost a race against another event. In that case, the conditional event is *not* appended to the log, and <xref:Orleans.EventSourcing.JournaledGrain`2.RaiseConditionalEvent*> returns `false`.

This is analogous to using e-tags with conditional storage updates and provides a simple mechanism to avoid committing conflicting events.

It's possible and sensible to use both conditional and unconditional events for the same grain, such as `DepositEvent` and `WithdrawalEvent`. Deposits don't need to be conditional: even if a `DepositEvent` loses a race, it doesn't have to be canceled but can still be appended to the global event sequence.

Awaiting the task returned by <xref:Orleans.EventSourcing.JournaledGrain`2.RaiseConditionalEvent*> is sufficient to confirm the event; you don't need to call <xref:Orleans.EventSourcing.JournaledGrain`2.ConfirmEvents*> as well.

## Explicit synchronization

Sometimes, you might want to ensure a grain is fully caught up with the latest version. You can enforce this by calling:

```csharp
await RefreshNow();
```

This call does two things:

1. Confirms all unconfirmed events.
1. Loads the latest version from storage.
