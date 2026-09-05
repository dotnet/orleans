---
title: Write a custom persistent-stream queue adapter
description: Implement and register an Orleans persistent-stream queue adapter for an external queue technology.
ms.date: 08/18/2026
ms.topic: how-to
---

# Write a custom persistent-stream queue adapter

Use a custom queue adapter to connect the Orleans persistent-stream runtime to a queue technology with its own transport and delivery semantics. The adapter translates between Orleans stream batches and the external queue. Orleans supplies the persistent stream provider, pulling agents, subscription routing, queue balancing, and cache management.

When Azure Queue Storage or Azure Event Hubs already provides the required transport behavior, a [data adapter](data-adapters.md) customizes its wire format while retaining the built-in queue adapter.

Register an <xref:Orleans.Streams.IQueueAdapterFactory> with `AddPersistentStreams`; Orleans creates and hosts <xref:Orleans.Providers.Streams.Common.PersistentStreamProvider>.

Before implementing an adapter, understand the [persistent stream pulling architecture](../implementation/streams-implementation/index.md). In particular, decide the adapter's delivery, acknowledgement, ordering, partitioning, and rewind semantics.

## Implement the transport boundary

Keep the queue SDK and wire-format logic behind a transport abstraction. The transport must:

- serialize the stream ID, event payloads, and request context into an evolvable envelope;
- assign a monotonically ordered sequence number within each queue partition;
- return only messages from the requested partition;
- acknowledge or delete messages only after Orleans calls the completion method; and
- surface queue failures instead of returning a successful empty read.

:::code language="csharp" source="snippets/streaming/CustomQueueAdapter.cs" id="custom_queue_transport":::

The sequence number must remain stable when the queue redelivers a message. The example assigns sequence numbers in the transport, so it rejects caller-supplied sequence tokens when producing events.

## Implement the adapter and receiver

<xref:Orleans.Streams.IQueueAdapter> handles writes and creates one receiver per queue partition. The stream-to-queue mapper used for writes must be the same mapper returned by the factory.

:::code language="csharp" source="snippets/streaming/CustomQueueAdapter.cs" id="custom_queue_adapter":::

<xref:Orleans.Streams.IQueueAdapterReceiver> reads queue messages and acknowledges them after every consumer has processed them. If the queue uses visibility leases, renew them while Orleans retains the message and make shutdown cancel outstanding reads.

:::code language="csharp" source="snippets/streaming/CustomQueueAdapter.cs" id="custom_queue_receiver":::

The batch container restores the stream identity, per-event sequence tokens, and request context when Orleans delivers the batch.

:::code language="csharp" source="snippets/streaming/CustomQueueAdapter.cs" id="custom_queue_batch":::

## Implement the factory

The factory composes the adapter with queue mapping, caching, and failure handling. Use named options because one process can register multiple providers with different names.

:::code language="csharp" source="snippets/streaming/CustomQueueAdapter.cs" id="custom_queue_factory":::

`SimpleQueueAdapterCache` is suitable for a non-rewindable adapter whose queue remains the durability boundary. A rewindable adapter usually needs a cache and sequence-token implementation which can position cursors at retained historical messages.

Custom pooled caches use <xref:Orleans.Providers.Streams.Common.ICacheDataAdapter.Compare*> to position cursors against cached messages. The default implementation compares `SequenceNumber` and `EventIndex`, preserving the numeric ordering used by existing providers. Override it when the authoritative provider position is encoded in <xref:Orleans.Providers.Streams.Common.CachedMessage.Segment>; return an order consistent with the token produced by <xref:Orleans.Providers.Streams.Common.ICacheDataAdapter.GetSequenceToken*> so cache bounds, block selection, and cache-miss detection use the same position contract.

`AddPersistentStreams` leaves checkpointing to the adapter. The non-rewindable example acknowledges completed messages through its receiver and therefore has no independent checkpoint. For a retained-log transport, implement an <xref:Orleans.Streams.IStreamQueueCheckpointerFactory>, have the receiver or cache load and update the per-partition position, and register it as a named component with `ConfigureComponent`. Persist a checkpoint only after all consumers have advanced beyond the corresponding cached messages. A no-op checkpointer is suitable only when replay position is deliberately disposable.

## Register the provider

Register the transport client in dependency injection, then pass the factory's `Create` method to `AddPersistentStreams`. Configure queue count and cache capacity through the provider configurator.

:::code language="csharp" source="snippets/streaming/CustomQueueAdapter.cs" id="custom_queue_silo_registration":::

Register the same provider name and compatible mapping on Orleans clients which directly produce or consume streams:

:::code language="csharp" source="snippets/streaming/CustomQueueAdapter.cs" id="custom_queue_client_registration":::

Keep the provider name and partition count stable. Changing either can map an existing stream to a different queue and strand previously enqueued messages. Configure durable `PubSubStore` grain storage for explicit subscriptions in production; `PubSubStore` preserves subscription records independently from queue durability.

## Validate failure behavior

Test the adapter against the real queue service, including:

1. batches containing multiple event types and request-context values;
1. empty reads, cancellation, transient errors, throttling, and shutdown;
1. producer, receiver, and silo failure before and after acknowledgement;
1. queue ownership moving between silos during membership changes;
1. duplicate delivery and consumer idempotency;
1. stable stream-to-partition mapping across restarts and upgrades; and
1. sustained load beyond cache capacity to verify backpressure and queue retention.

Monitor queue depth and oldest-message age by partition, receive and acknowledgement latency, redelivery count, throttling, pulling-agent errors, and consumer delivery failures. Alert before retention or visibility limits can cause data loss or a redelivery storm.
