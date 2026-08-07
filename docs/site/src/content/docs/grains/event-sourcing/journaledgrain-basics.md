---
title: The JournaledGrain API
description: Define state transitions and confirm events using JournaledGrain.
ms.date: 08/02/2026
ms.topic: concept-article
---

# The journaled grain API

Derive an event-sourced grain from <xref:Orleans.EventSourcing.JournaledGrain`2>:

```csharp
public sealed class AccountGrain
    : JournaledGrain<AccountState, AccountEvent>, IAccountGrain
{
}
```

`TGrainState` must be a class with a public parameterless constructor. `TEventBase` is the common class or interface for the grain's events. State and event types must be serializable because providers can persist or send them.

The one-parameter <xref:Orleans.EventSourcing.JournaledGrain`1> form uses `object` as the event base type.

## Confirmed and tentative state

- <xref:Orleans.EventSourcing.JournaledGrain`2.State> contains only confirmed events.
- <xref:Orleans.EventSourcing.JournaledGrain`2.Version> is the number of confirmed events.
- <xref:Orleans.EventSourcing.JournaledGrain`2.TentativeState> also includes locally submitted, unconfirmed events.
- <xref:Orleans.EventSourcing.JournaledGrain`2.UnconfirmedEvents> returns the current unconfirmed suffix.

Don't mutate <xref:Orleans.EventSourcing.JournaledGrain`2.State> or <xref:Orleans.EventSourcing.JournaledGrain`2.TentativeState> directly. Change state by raising events.

## Define transitions

By default, Orleans dynamically invokes the closest `Apply` overload on the state:

```csharp
[GenerateSerializer]
public sealed class AccountState
{
    [Id(0)]
    public decimal Balance { get; private set; }

    public void Apply(Deposited deposited) =>
        Balance += deposited.Amount;

    public void Apply(Withdrawn withdrawn) =>
        Balance -= withdrawn.Amount;
}
```

Alternatively, override <xref:Orleans.EventSourcing.JournaledGrain`2.TransitionState*>. Transition logic must be deterministic and must only mutate the supplied state. Providers can replay transitions more than once, so don't perform I/O or other side effects from transition methods.

## Raise and confirm events

<xref:Orleans.EventSourcing.JournaledGrain`2.RaiseEvent*> submits an event but doesn't wait for durable confirmation:

```csharp
RaiseEvent(new Deposited(amount));
await ConfirmEvents();
```

Await <xref:Orleans.EventSourcing.JournaledGrain`2.ConfirmEvents*> before returning when the grain method promises that its events are confirmed. If confirmation isn't awaited, Orleans continues confirmation in the background and callers can observe tentative behavior.

Submit a related sequence atomically with <xref:Orleans.EventSourcing.JournaledGrain`2.RaiseEvents*>:

```csharp
RaiseEvents(events);
await ConfirmEvents();
```

The provider submits the sequence as one log append. The confirmed version advances by the number of events.

## Conditional events

Use <xref:Orleans.EventSourcing.JournaledGrain`2.RaiseConditionalEvent*> or <xref:Orleans.EventSourcing.JournaledGrain`2.RaiseConditionalEvents*> when an event is valid only against the version currently observed:

```csharp
if (!await RaiseConditionalEvent(new Withdrawn(amount)))
{
    return false;
}
```

The returned task completes after the conditional append is resolved. `false` means another update won the version race and the event wasn't appended. Re-evaluate the command using the refreshed state; don't treat a conflict as success.

## Synchronize and retrieve events

<xref:Orleans.EventSourcing.JournaledGrain`2.RefreshNow*> confirms submitted events and refreshes the view from storage:

```csharp
await RefreshNow();
```

<xref:Orleans.EventSourcing.JournaledGrain`2.RetrieveConfirmedEvents*> returns a confirmed segment only when the provider retains and exposes it. State storage and custom storage don't expose events through this API; log storage does.

<xref:Orleans.EventSourcing.JournaledGrain`2.ClearLogAsync*> resets state and discards confirmed and unconfirmed events only when supported by the provider. Clearing a log is destructive and isn't a schema-migration mechanism.

## Evolve state and events

For replay-based storage, historical events remain part of the durable contract. Keep their serialized shape readable and preserve transition behavior, or introduce explicit upcasting/migration in custom storage. Snapshot storage instead requires the stored state snapshot to remain readable. Test both activation and replay using production-shaped historical data.
