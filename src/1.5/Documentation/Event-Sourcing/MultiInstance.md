---
layout: page
title: Replicated Grains
---

## Replicated Grains

Sometimes, there can be multiple instances of the same grain active, such as when operating a multi-cluster, and using the `[OneInstancePerCluster]` attribute. The JournaledGrain is designed to support replicated instances with minimal friction. It relies on *log-consistency providers* to run the necessary protocols to ensure all instances agree on the same sequence of events. In particular, it takes care of the following aspects: 

* **Consistent Versions**: All versions of the grain state (except for tentative versions) are based on the same global sequence of events. In particular, if two instances see the same version number, then they see the same state.

* **Racing Events**: Multiple instances can simultaneously raise an event. The consistency provider resolves this race and ensures everyone agrees on the same sequence.

* **Notifications/Reactivity**: After an event is raised at one grain instance, the consistency provider not only updates storage, but also notifies all the other grain instances.

For a general discussion of the consistency model see our [TechReport](https://www.microsoft.com/en-us/research/publication/geo-distribution-actor-based-services/) and the [GSP paper](https://www.microsoft.com/en-us/research/publication/global-sequence-protocol-a-robust-abstraction-for-replicated-shared-state-extended-version/) (Global Sequence Protocol).

## Conditional Events

Racing events can be problematic if they have a conflict, i.e. should not both commit for some reason. For example, when withdrawing money from a bank account, two instances may independently determine that there are sufficient funds for a withdrawal, and issue a withdrawal event. But the combination of both events could overdraw. To avoid this, the JournaledGrain API supports a `RaiseConditionalEvent` method. 

```csharp
bool success = await RaiseConditionalEvent(new WithdrawalEvent()  { ... });
```

Conditional events double-check if the local version matches the version in storage. If not, it means the event sequence has grown in the meantime, which means this event has lost a race against some other event. In that case, the conditional event is *not* appended to the log, and `RaiseConditionalEvent` returns false.

This is the analogue of using e-tags with conditional storage updates, and likewise provides a simple mechanism to avoid committing conflicting events. 

It is possible and sensible to use both conditional and unconditional events for the same grain, such as a `DepositEvent` and a `WithdrawalEvent`. Deposits need not be conditional: even if a `DepositEvent` loses a race, it does not have to be cancelled, but can still be appended to the global event sequence. 

Awaiting the task returned by `RaiseConditionalEvent` is sufficient to confirm the event, i.e. it is not necessary to also call `ConfirmEvents`.

## Explicit Synchronization

Sometimes, it is desirable to ensure that a grain is fully caught up with the latest version. This can be enforced by calling

```csharp
await RefreshNow();
```

which both (1) confirms all unconfirmed events, and (2) loads the latest version from storage.

