---
title: Orleans streaming APIs
description: Work with stream identities, producers, consumers, and explicit or implicit subscriptions in Orleans 10.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Orleans streaming APIs

## Stream identity

An Orleans stream is selected by three application-level choices:

- **Provider name** selects a configured <xref:Orleans.Streams.IStreamProvider>.
- **Stream namespace** groups related streams and participates in implicit-subscription matching.
- **Stream key** identifies one stream within the namespace and can be a string, GUID, or integer.

Together, namespace and key form <xref:Orleans.Runtime.StreamId>. Keep identity construction in shared code:

:::code language="csharp" source="snippets/streaming/BasicStreaming.cs" id="stream_identity":::

`GetStream<T>` returns a typed <xref:Orleans.Streams.IAsyncStream`1>. Getting the provider and stream handles is local and doesn't create a broker entity. Producers and consumers must agree on `T`; Orleans serialization rules apply to payloads.

## Producers

Any grain or configured Orleans client can publish. A stream can have multiple producers:

:::code language="csharp" source="snippets/streaming/BasicStreaming.cs" id="stream_producer":::

Await each `OnNextAsync` call when publication order matters. The returned task reports provider acceptance, not end-to-end consumer completion. Provider failures and ambiguous timeouts require application-specific retry and deduplication decisions.

## Consumers

Consumers attach an <xref:Orleans.Streams.IAsyncObserver`1> implementation or callback delegates. `OnNextAsync` receives the item and, when the provider supplies one, a <xref:Orleans.Streams.StreamSequenceToken>.

Complete the consumer task only after the application has accepted responsibility for the item. Persistent providers use that completion to advance delivery or retry after failure. Avoid blocking threads; asynchronous consumer work naturally applies backpressure to that subscription.

Streams are multicast. Each subscription receives each item, and one grain can create multiple explicit subscriptions to the same stream. Each subscription has its own <xref:Orleans.Streams.StreamSubscriptionHandle`1>.

## Explicit and implicit subscriptions

### Explicit subscriptions

Use an explicit subscription when application behavior decides whether and when a grain or client subscribes. `SubscribeAsync` creates a new subscription every time, so activation code must resume existing handles rather than subscribe again:

:::code language="csharp" source="snippets/streaming/ExplicitSubscriptions.cs" id="explicit_subscription_grain":::

The subscription belongs to the grain identity, not one activation. After deactivation, a later activation calls `GetAllSubscriptionHandles` and `ResumeAsync` to attach its new observer instance. Call `UnsubscribeAsync` to remove a subscription.

This lifecycle is durable across cluster restarts only when the configured [`PubSubStore` is durable](pubsub-storage.md). A memory `PubSubStore` preserves records only while that cluster state remains available.

### Implicit subscriptions

Use an implicit subscription when a stream item should activate a grain determined by the stream identity:

:::code language="csharp" source="snippets/streaming/ImplicitSubscriptions.cs" id="implicit_subscription_grain":::

<xref:Orleans.ImplicitStreamSubscriptionAttribute> selects stream namespaces. For each matching grain type, Orleans maps the stream key to the grain key. Implementing <xref:Orleans.Streams.Core.IStreamSubscriptionObserver> lets Orleans supply the implicit handle; call `ResumeAsync` once to attach processing logic.

Implicit subscriptions are declared in grain metadata. They aren't created by `SubscribeAsync`, can't be individually removed at runtime, and don't support multiple subscriptions for the same grain binding.

## Clients

Clients can produce and explicitly consume streams after the provider is configured on <xref:Orleans.Hosting.IClientBuilder>. Client subscriptions are tied to the connected client process and must be re-established after reconnecting or restarting. Implicit subscriptions target grains, not clients.

For failure behavior and sequence tokens, continue to [Delivery, ordering, replay, and recovery](delivery-semantics.md).
