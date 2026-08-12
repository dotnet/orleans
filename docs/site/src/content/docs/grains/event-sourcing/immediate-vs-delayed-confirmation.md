---
title: Immediate and delayed event confirmation
description: Choose confirmation and scheduling semantics for JournaledGrain.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Immediate and delayed event confirmation

<xref:Orleans.EventSourcing.JournaledGrain`2.RaiseEvent*> updates the tentative view and starts submission. Confirmation determines when the event joins the durable, ordered log.

## Immediate confirmation

Await <xref:Orleans.EventSourcing.JournaledGrain`2.ConfirmEvents*> before returning when the grain method promises a confirmed result:

:::code language="csharp" source="../../snippets/compiled/EventSourcing/EventSourcingSnippets.cs" id="immediate_confirmation":::

Also await conditional-event tasks. Without reentrant or interleavable calls, this prevents another turn from observing the grain between submission and confirmation.

The tradeoff is availability and latency: the call waits for the selected provider and backing store. A connectivity problem can hold confirmation while the protocol retries.

## Delayed confirmation

A grain can return without awaiting <xref:Orleans.EventSourcing.JournaledGrain`2.ConfirmEvents*>, or permit interleaving while confirmation is pending. This can improve throughput but changes what the method guarantees.

Use:

- <xref:Orleans.EventSourcing.JournaledGrain`2.State> for the confirmed view.
- <xref:Orleans.EventSourcing.JournaledGrain`2.TentativeState> for confirmed plus locally unconfirmed events.
- <xref:Orleans.EventSourcing.JournaledGrain`2.UnconfirmedEvents> for the pending suffix.

Tentative state isn't a durable promise. An activation can fail before confirmation, and competing updates can change the final ordering or reject a conditional event.

## Orleans scheduling still applies

Reentrancy doesn't run two grain turns simultaneously on different threads. State can change across an `await` because another turn can run while the first is suspended. Capture values needed across an `await` or re-check the state afterward.

Choose and document one semantic per grain method: confirmed-before-return or accepted-for-background-confirmation. Callers shouldn't have to infer durability from implementation details.
