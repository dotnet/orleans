---
title: JournaledGrain state notifications
description: React to confirmed and tentative JournaledGrain state changes.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Journaled grain state notifications

Override <xref:Orleans.EventSourcing.JournaledGrain`2.OnStateChanged*> to react after the confirmed version can have increased:

```csharp
protected override void OnStateChanged()
{
    // Inspect State and Version.
}
```

It can run after loading a newer state, confirming a local event, or receiving a protocol notification. It isn't an exactly-once integration-event callback. Don't perform an external side effect here unless the application adds its own idempotency or outbox protocol.

Override <xref:Orleans.EventSourcing.JournaledGrain`2.OnTentativeStateChanged*> to react when the tentative view changes:

```csharp
protected override void OnTentativeStateChanged()
{
    // Inspect TentativeState and UnconfirmedEvents.
}
```

<xref:Orleans.EventSourcing.JournaledGrain`2.RaiseEvent*> triggers this callback. It can also run when confirmation or synchronization changes the tentative suffix.

Both callbacks execute under Orleans turn-based scheduling. They should be quick and must not assume that their observed state remains unchanged across a later `await`.
