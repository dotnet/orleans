---
title: Stream delivery, ordering, replay, and recovery
description: Design Orleans streaming consumers for provider-specific delivery, ordering, replay, and failure behavior.
ms.date: 08/24/2026
ms.topic: concept-article
---

# Stream delivery, ordering, replay, and recovery

The Orleans stream API doesn't promise one universal delivery guarantee. The selected provider and its backing service determine when an event is accepted, whether failed delivery is retried, what order is observable, and whether old events remain available.

Orleans doesn't guarantee exactly-once delivery or processing by itself. Network failures can make publication outcomes ambiguous, and persistent providers can redeliver events.

## Exactly-once effects

An application can achieve exactly-once observable effects within a transactional boundary. Identify each event with a stable value, such as an application-defined event ID or a provider <xref:Orleans.Streams.StreamSequenceToken> only when the provider guarantees that the token is stable across redelivery. Atomically commit both the event's effects and the fact that the identity was processed. On redelivery, skip an identity which was already committed.

Recording a sequence token alone isn't sufficient. If the consumer records the token before applying the effect, a failure between those operations can lose the effect. If it applies the effect before recording the token, a failure can cause the effect to be applied again. The effect and checkpoint must share one atomic commit, or the effect destination must provide equivalent deduplication.

For effects outside that transaction, use an idempotency key, transactional inbox or outbox, or a destination which rejects duplicate event IDs. The scope and stability of sequence tokens are provider-specific. Tokens from non-rewindable queue providers can represent a read position and can change when an event is redelivered. Use an application event ID unless the provider explicitly guarantees a token which is suitable for deduplication.

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
- A stateless worker stream subscription assigns each delivery attempt to one selected activation. Concurrent deliveries and retries can run on different activations, so completion order across the worker pool is unspecified.

Use event version numbers or domain sequence numbers when business logic requires ordering. A <xref:Orleans.Streams.StreamSequenceToken> represents provider position; it isn't a universal business sequence and not every provider accepts application-supplied tokens.

## Replay and rewindability

A rewindable provider can start or resume a subscription from a provider sequence token while the corresponding event remains in that provider's retention window. Rewindability isn't the same as durability:

- Memory streams are rewindable only over their transient in-memory cache.
- Event Hubs, RabbitMQ Streams, and Redis Streams are rewindable over retained external data.
- Azure Queue, Amazon SQS, ADO.NET, and NATS JetStream providers aren't rewindable.

Explicit subscriptions can resume from retained positions according to the provider's token semantics. An implicit subscription accepts a recovery token when an activation attaches its observer, then advances monotonically for that attachment. Once a delivery call completes successfully, resuming the active implicit handle with a sequence token throws <xref:System.InvalidOperationException>; passing `null` replaces the observer at its current position.

New explicit subscriptions to rewindable persistent streams can also select [a cache-relative start position](subscription-start-positions.md). See the [provider matrix](stream-providers.md#provider-matrix) for provider capabilities.

## Recovery checklist

1. Use a durable provider when accepted events must survive silo or cluster loss.
1. Use a durable [`PubSubStore`](pubsub-storage.md) when explicit subscription records must survive cluster loss.
1. Resume existing explicit handles after grain activation; don't call <xref:Orleans.Streams.IAsyncObservable`1.SubscribeAsync*> unconditionally.
1. Persist the last applied domain position when the application needs deterministic recovery.
1. For a rewindable provider, restart from a checkpointed token and tolerate replay of the checkpoint boundary.
1. Make effects idempotent and alert on poison events, repeated retries, and growing lag.

Provider retention and subscription storage solve different problems. Durable events without durable subscription metadata can wait with no consumer binding; durable subscriptions with a transient provider can't recover events that disappeared.
