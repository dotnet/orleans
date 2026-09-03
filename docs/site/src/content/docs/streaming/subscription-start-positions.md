---
title: Choose a persistent-stream subscription start position
description: Start an Orleans persistent-stream subscription with new messages or replay messages retained in the local queue cache.
ms.date: 09/03/2026
ms.topic: how-to
---

# Choose a persistent-stream subscription start position

A rewindable persistent-stream subscription can start with newly published messages or replay messages already retained by its pulling agent. Choose the start position when application behavior creates the subscription, or configure a provider-wide default for subscriptions which omit a position. After delivery begins, sequence-token progress defines the subscription's position.

For the broader subscription model, see [Orleans streaming APIs](streams-programming-apis.md). Provider durability and rewindability are described in [Orleans stream providers](stream-providers.md).

## Choose a position for one subscription

Pass a <xref:Orleans.Streams.StreamSubscriptionStartPosition> to <xref:Orleans.Streams.AsyncObservableExtensions.SubscribeAsync*>:

:::code language="csharp" source="snippets/subscription-start-positions/SubscriptionStartPositions.cs" id="subscribe_earliest_available":::

The batch-observer overload accepts the same position:

:::code language="csharp" source="snippets/subscription-start-positions/SubscriptionStartPositions.cs" id="subscribe_batch_earliest_available":::

The values have these semantics:

| Position | Initial delivery |
|---|---|
| <xref:Orleans.Streams.StreamSubscriptionStartPosition.Latest> | Messages published after the subscription is established. Messages already retained in the queue cache are skipped. This value preserves the default behavior of a tokenless `SubscribeAsync` call. |
| <xref:Orleans.Streams.StreamSubscriptionStartPosition.EarliestAvailable> | The earliest matching message currently retained in the pulling agent's local queue cache, included in delivery. When the cache has no matching message, delivery begins with the stream's first future message. |

The position applies when the subscription is created. Resume an existing subscription handle to continue from its established progress; resume APIs use sequence tokens rather than a new start position.

## Configure the provider default

Configure <xref:Orleans.Configuration.StreamPullingAgentOptions.InitialSubscriptionStartPosition> on the silo-side persistent-stream provider:

:::code language="csharp" source="snippets/subscription-start-positions/SubscriptionStartPositions.cs" id="configure_default_start_position":::

This setting controls initial subscriptions which omit both a concrete sequence token and an explicit start position. It applies to tokenless explicit subscriptions and initial implicit-subscription attachments which have no delivery progress. <xref:Orleans.Streams.StreamSubscriptionStartPosition.Latest> is the default.

Orleans chooses an initial position in this order:

1. A concrete <xref:Orleans.Streams.StreamSequenceToken> supplied by the caller.
1. An explicit <xref:Orleans.Streams.StreamSubscriptionStartPosition> supplied to `SubscribeAsync`.
1. <xref:Orleans.Configuration.StreamPullingAgentOptions.InitialSubscriptionStartPosition>.
1. <xref:Orleans.Streams.StreamSubscriptionStartPosition.Latest>.

An explicit choice therefore overrides the provider default in either direction. Explicit `Latest` skips retained messages when the provider default is `EarliestAvailable`, and explicit `EarliestAvailable` replays the local cache when the provider default is `Latest`. Configure the option consistently on every silo eligible to host a pulling agent for the named provider.

## Understand the available replay window

`EarliestAvailable` searches one pulling agent's local queue cache for the target <xref:Orleans.Runtime.StreamId>. The cache's current contents define the available replay window. Cache eviction, memory pressure, queue assignment, silo restarts, and the time since the pulling agent began reading can all move its earliest available position forward.

For Azure Event Hubs, `EarliestAvailable` keeps the partition receiver and its checkpoint at their current positions. It replays matching messages which the Orleans Event Hubs pulling agent has already read and still retains in its silo-side cache. The local cache window is therefore the replay window for this API, while Event Hubs retention remains the upstream recovery window.

<xref:Orleans.Configuration.StreamCacheEvictionOptions.DataMinTimeInCache> and <xref:Orleans.Configuration.StreamCacheEvictionOptions.DataMaxAgeInCache> control time-based eviction, while cache pressure can advance the earliest retained position. Size Event Hubs retention for upstream recovery and size the Orleans cache for the replay interval required by new subscriptions. See [Protect a slow Event Hubs consumer](streaming-operations.md#protect-a-slow-event-hubs-consumer) for cache eviction and pressure behavior.

## Handle queue cache support

The built-in pooled, simple, memory, generator, and Event Hubs queue caches support `EarliestAvailable`. A custom persistent-stream cache participates by implementing <xref:Orleans.Streams.IQueueCache.GetCacheCursorAtPosition*>.

The default queue-cache interface behavior maps `Latest` to the existing tokenless cursor path and reports <xref:System.NotSupportedException> for `EarliestAvailable`. An explicit subscription which requests the unsupported position receives the error and is faulted. When `EarliestAvailable` is the provider default for an implicit subscription, Orleans reports the error to the consumer and keeps the implicit subscription live at its current position.

A custom <xref:Orleans.Streams.IAsyncObservable`1> implementation receives equivalent extension-overload compatibility: `Latest` uses its tokenless subscription path, and `EarliestAvailable` reports <xref:System.NotSupportedException> until the observable implements Orleans start-position subscriptions.

## Roll out start-position subscriptions

During a rolling upgrade, first deploy the supporting Orleans version to every silo eligible to host persistent-stream pulling agents. Keep tokenless subscriptions at `Latest` until those silos are upgraded. Then deploy consumers which create subscriptions with explicit start positions and change `InitialSubscriptionStartPosition` as required. This sequence ensures that every pulling agent which receives the subscription understands and applies its requested position.

For delivery guarantees and sequence-token recovery, continue to [Stream delivery, ordering, replay, and recovery](delivery-semantics.md).
