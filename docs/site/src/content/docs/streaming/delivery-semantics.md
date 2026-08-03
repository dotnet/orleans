---
title: Stream delivery, ordering, replay, and recovery
description: Design Orleans streaming consumers for provider-specific delivery, ordering, replay, and failure behavior.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Stream delivery, ordering, replay, and recovery

The Orleans stream API doesn't promise one universal delivery guarantee. The selected provider and its backing service determine when an event is accepted, whether failed delivery is retried, what order is observable, and whether old events remain available.

Orleans doesn't provide an exactly-once processing guarantee. Network failures can make publication outcomes ambiguous, persistent providers can redeliver, and a consumer can fail after applying a side effect but before acknowledging completion. Design consumers to be idempotent or record deduplication state atomically with their effects.

## Producer acknowledgment

The task returned by <xref:Orleans.Streams.IAsyncObserver`1.OnNextAsync*> completes when the provider has accepted responsibility for the item according to that provider's contract. For a durable provider, this is normally after the backing service accepts the event. It isn't a receipt proving that all subscribers completed processing.

A producer timeout or exception means acceptance wasn't confirmed. The item might not have been accepted, or the acknowledgment might have been lost. Retrying can create a duplicate, so include an application event ID when retries are possible.

## Consumer acknowledgment and retries

A consumer's `OnNextAsync` task signals when that consumer has accepted responsibility for the item. Queue-backed providers generally retain or redeliver an item when delivery fails before successful completion. Retries can delay later items and can change observed order.

Don't acknowledge before required state changes or side effects are safely recorded. Don't hold the task open for unrelated background work either; that consumes delivery capacity and increases queue and cache pressure.

## Ordering

Treat ordering as scoped, not global:

- Awaiting publication calls serializes one producer's submissions, but concurrent producers have no shared order.
- Physical partitions and queues order independently.
- Retries, visibility timeouts, consumer failures, and rebalancing can reorder delivery.
- A grain processes its own turns serially unless its concurrency configuration says otherwise, but that doesn't impose a total order across grains or subscriptions.

Use event version numbers or domain sequence numbers when business logic requires ordering. A <xref:Orleans.Streams.StreamSequenceToken> represents provider position; it isn't a universal business sequence and not every provider accepts application-supplied tokens.

## Replay and rewindability

A rewindable provider can start or resume a subscription from a provider sequence token while the corresponding event remains in that provider's retention window. Rewindability isn't the same as durability:

- Memory streams are rewindable only over their transient in-memory cache.
- Event Hubs and Redis Streams are rewindable over retained external data.
- Azure Queue, Amazon SQS, ADO.NET, and NATS JetStream providers aren't rewindable.

See the [provider matrix](stream-providers.md#provider-matrix) for provider capabilities.

## Recovery checklist

1. Use a durable provider when accepted events must survive silo or cluster loss.
1. Use a durable [`PubSubStore`](pubsub-storage.md) when explicit subscription records must survive cluster loss.
1. Resume existing explicit handles after grain activation; don't call `SubscribeAsync` unconditionally.
1. Persist the last applied domain position when the application needs deterministic recovery.
1. For a rewindable provider, restart from a checkpointed token and tolerate replay of the checkpoint boundary.
1. Make effects idempotent and alert on poison events, repeated retries, and growing lag.

Provider retention and subscription storage solve different problems. Durable events without durable subscription metadata can wait with no consumer binding; durable subscriptions with a transient provider can't recover events that disappeared.
