---
title: Notifications
description: Learn the concepts of notifications in .NET Orleans.
ms.date: 05/23/2025
ms.topic: concept-article
---

# Notifications

It's often convenient to react to state changes. All callbacks are subject to Orleans' turn-based guarantees; see also the section on [Concurrency Guarantees](immediate-vs-delayed-confirmation.md#concurrency-guarantees).

## Track confirmed state

To be notified of any changes to the confirmed state, `JournaledGrain` subclasses can override this method:

```csharp
protected override void OnStateChanged()
{
   // read state and/or event log and take appropriate action
}
```

`OnStateChanged` is called whenever the confirmed state updates, i.e., the version number increases. This can happen when:

1. A newer version of the state was loaded from storage.
1. An event raised by this instance has been successfully written to storage.
1. A notification message was received from some other instance.

Note that since all grains initially have version zero, `OnStateChanged` is called whenever the initial load from storage completes with a version larger than zero.

## Track tentative state

To be notified of any changes to the tentative state, `JournaledGrain` subclasses can override this method:

```csharp
protected override void OnTentativeStateChanged()
{
   // read state and/or events and take appropriate action
}
```

`OnTentativeStateChanged` is called whenever the tentative state changes, i.e., if the combined sequence (`ConfirmedEvents` + `UnconfirmedEvents`) changes. In particular, a callback to `OnTentativeStateChanged()` always happens during <xref:Orleans.EventSourcing.JournaledGrain`2.RaiseEvent*>.
