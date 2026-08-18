---
title: Orleans streaming APIs
description: Work with stream identities, producers, consumers, and explicit or implicit subscriptions in Orleans.
ms.date: 08/18/2026
ms.topic: concept-article
---

# Orleans streaming APIs

## Stream identity

<a id="async-stream"></a>

An Orleans stream is selected by three application-level choices:

- **Provider name** selects a configured <xref:Orleans.Streams.IStreamProvider>.
- **Stream namespace** groups related streams and participates in implicit-subscription matching.
- **Stream key** identifies one stream within the namespace and can be a string, GUID, or integer.

Together, namespace and key form <xref:Orleans.Runtime.StreamId>. Keep identity construction in shared code:

:::code language="csharp" source="snippets/streaming/BasicStreaming.cs" id="stream_identity":::

<xref:Orleans.Streams.IStreamProvider.GetStream*> returns a typed <xref:Orleans.Streams.IAsyncStream`1>. Getting the provider and stream handles is local and doesn't create a broker entity. Producers and consumers must agree on `T`; Orleans serialization rules apply to payloads.

## Producers

<a id="producing-and-consuming"></a>

Any grain or configured Orleans client can publish. A stream can have multiple producers:

:::code language="csharp" source="snippets/streaming/BasicStreaming.cs" id="stream_producer":::

Await each <xref:Orleans.Streams.IAsyncObserver`1.OnNextAsync*> call when publication order matters. The returned task reports provider acceptance, not end-to-end consumer completion. Provider failures and ambiguous timeouts require application-specific retry and deduplication decisions.

## Consumers

<a id="multiplicity"></a>

Consumers attach an <xref:Orleans.Streams.IAsyncObserver`1> implementation or callback delegates. `OnNextAsync` receives the item and, when the provider supplies one, a <xref:Orleans.Streams.StreamSequenceToken>.

Complete the consumer task only after the application has accepted responsibility for the item. Persistent providers use that completion to advance delivery or retry after failure. Avoid blocking threads; asynchronous consumer work naturally applies backpressure to that subscription.

Streams are multicast. Each subscription receives each item, and one grain can create multiple explicit subscriptions to the same stream. Each subscription has its own <xref:Orleans.Streams.StreamSubscriptionHandle`1>.

## Explicit and implicit subscriptions

The provider's <xref:Orleans.Streams.StreamPubSubType> controls which subscription models are available:

| Value | Choose this value when | Tradeoff |
|---|---|---|
| `ExplicitGrainBasedAndImplicit` (default) | The application uses both models or expects its subscription requirements to evolve. | Provides the most flexibility. Producer registration checks both the grain-based rendezvous and implicit grain metadata, and the explicit portion requires a [`PubSubStore`](pubsub-storage.md). |
| `ExplicitGrainBasedOnly` | Every consumer uses runtime-created subscriptions, including client consumers. | Focuses discovery on explicit subscriptions. Subscription and producer changes use rendezvous grains and `PubSubStore`, and the application manages subscription handles and recovery. |
| `ImplicitOnly` | Every consumer is a grain declared with <xref:Orleans.ImplicitStreamSubscriptionAttribute>. | Uses cluster grain metadata as its subscription directory, with zero rendezvous-grain calls and zero `PubSubStore` operations. This gives it the lowest pub/sub control-plane overhead and makes it attractive wherever metadata-defined grain subscriptions fit. Runtime-created, individually removable, and client subscriptions use an explicit-capable mode. |

The mode determines the subscription discovery, coordination, and storage work. Event transport and delivery follow the selected stream provider. Specialized modes can reduce control-plane overhead; choose a mode based on the required subscription semantics and measure the effect in the application's workload.

### Change the configured mode

Apply a pub/sub type change by updating <xref:Orleans.Hosting.PersistentStreamConfiguratorExtensions.ConfigureStreamPubSub*> and restarting every silo and client which uses that named provider. Restart them as a coordinated deployment so every host uses the same value and computes the same subscription set.

Each subscription model retains its own lifecycle across a change:

- Implicit subscriptions come from grain metadata. `ExplicitGrainBasedOnly` selects explicit records, while either implicit-capable mode applies matching metadata.
- Explicit subscription records follow the configured `PubSubStore` durability. `ImplicitOnly` selects metadata-derived subscriptions and leaves retained explicit records in the store. Account for those records before changing modes. Re-enabling explicit support with the same service ID, provider name, and durable `PubSubStore` makes retained records available again; each activated consumer then resumes its handle.

Use the default combined mode when subscription requirements are expected to evolve and continuous support for both models outweighs the additional pub/sub control-plane work.

### Explicit subscriptions

<a id="recovering-from-failures"></a>
<a id="writing-subscription-logic"></a>
<a id="fully-managed-and-reliable-streaming-pub-sub"></a>

Use an explicit subscription when application behavior decides whether and when a grain or client subscribes. <xref:Orleans.Streams.IAsyncObservable`1.SubscribeAsync*> creates a new subscription every time, so activation code must resume existing handles rather than subscribe again:

:::code language="csharp" source="snippets/streaming/ExplicitSubscriptions.cs" id="explicit_subscription_grain":::

The subscription belongs to the grain identity, not one activation. After deactivation, a later activation calls <xref:Orleans.Streams.IAsyncStream`1.GetAllSubscriptionHandles*> and <xref:Orleans.Streams.StreamSubscriptionHandle`1.ResumeAsync*> to attach its new observer instance. Call <xref:Orleans.Streams.StreamSubscriptionHandle`1.UnsubscribeAsync*> to remove a subscription.

This lifecycle is durable across cluster restarts only when the configured [`PubSubStore` is durable](pubsub-storage.md). A memory `PubSubStore` preserves records only while that cluster state remains available.

### Implicit subscriptions

Use an implicit subscription when a stream item should activate a grain determined by the stream identity:

:::code language="csharp" source="snippets/streaming/ImplicitSubscriptions.cs" id="implicit_subscription_grain":::

<xref:Orleans.ImplicitStreamSubscriptionAttribute> selects stream namespaces. For each matching grain type, Orleans maps the stream key to the grain key. Implementing <xref:Orleans.Streams.Core.IStreamSubscriptionObserver> lets Orleans supply the implicit handle; call `ResumeAsync` once to attach processing logic.

Implicit subscriptions are declared in grain metadata. They aren't created through <xref:Orleans.Streams.IAsyncObservable`1.SubscribeAsync*>, can't be individually removed at runtime, and don't support multiple subscriptions for the same grain binding.

## Clients

<a id="grains-and-orleans-clients"></a>
<a id="configuration"></a>

Clients can produce and explicitly consume streams after the provider is configured on <xref:Orleans.Hosting.IClientBuilder>. Client subscriptions are tied to the connected client process and must be re-established after reconnecting or restarting. Implicit subscriptions target grains, not clients.

## Stateless worker grains

Grains marked with <xref:Orleans.Concurrency.StatelessWorkerAttribute> can publish stream events. Orleans rejects subscription attempts from them with an <xref:System.InvalidOperationException> because a stream consumer uses a grain extension which must bind to one activation, while a stateless worker grain identity can have multiple replaceable activations.

Use a regular grain as the stream consumer so that the stream-to-grain binding has a stable virtual identity which can own state and the subscription lifecycle. If processing after delivery is stateless and parallelizable, have that grain call stateless worker grains and await the required work before its consumer task completes. This keeps stream acknowledgment and recovery at the regular grain boundary instead of treating a multicast subscription as a competing-consumer work queue.

<a id="stream-order-and-sequence-tokens"></a>
<a id="rewindable-streams"></a>

For failure behavior and sequence tokens, continue to [Delivery, ordering, replay, and recovery](delivery-semantics.md).

For a larger compiled example, see [`SampleStreamingGrain.cs`](https://github.com/dotnet/orleans/blob/main/test/Grains/TestGrains/SampleStreamingGrain.cs) in the Orleans test suite.
