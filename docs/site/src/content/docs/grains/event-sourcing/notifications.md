---
title: JournaledGrain state notifications
description: React to confirmed and tentative JournaledGrain state changes.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Journaled grain state notifications

Override <xref:Orleans.EventSourcing.JournaledGrain`2.OnStateChanged*> to react after the confirmed version can have increased:

:::code language="csharp" source="../../snippets/compiled/EventSourcing/EventSourcingSnippets.cs" id="confirmed_state_changed":::

It can run after loading a newer state, confirming a local event, or receiving a protocol notification. It isn't an exactly-once integration-event callback. Don't perform an external side effect here unless the application adds its own idempotency or outbox protocol.

Override <xref:Orleans.EventSourcing.JournaledGrain`2.OnTentativeStateChanged*> to react when the tentative view changes:

:::code language="csharp" source="../../snippets/compiled/EventSourcing/EventSourcingSnippets.cs" id="tentative_state_changed":::

<xref:Orleans.EventSourcing.JournaledGrain`2.RaiseEvent*> triggers this callback. It can also run when confirmation or synchronization changes the tentative suffix.

Both callbacks execute under Orleans turn-based scheduling. They should be quick and must not assume that their observed state remains unchanged across a later `await`.
