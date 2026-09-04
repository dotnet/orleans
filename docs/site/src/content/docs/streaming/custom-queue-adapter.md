---
title: Write a custom persistent-stream queue adapter
description: Implement and register an Orleans persistent-stream queue adapter for an external queue technology.
ms.date: 09/03/2026
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

`SimpleQueueAdapterCache` is suitable when subscription cursors only use the live cache. A retained-history adapter can compose <xref:Orleans.Providers.Streams.Common.RecoverableStreamReceiver`1>, <xref:Orleans.Providers.Streams.Common.IRecoverableStreamReplaySourceFactory`1>, and <xref:Orleans.Providers.Streams.Common.IRecoverableStreamReplaySource`1>. The singleton live source owns the durable queue checkpoint. Each historical source owns an independent read position and cancellation lifetime.

The receiver returns <xref:Orleans.Streams.IAsyncQueueCacheCursor> when a token predates the live cache. Its non-reentrant `MoveNextAsync` call distinguishes an available item, a temporary provider tail, and completed handoff to live delivery. The receiver bounds concurrent readers, queued admissions, replay cache capacity, and read batch size through <xref:Orleans.Configuration.RecoverableStreamReplayOptions>. Overlapping cursors reuse retained partition fragments when their ranges are compatible.

Use provider tokens which encode enough identity to reject a token from another provider or partition. Validate retention and establish any external replay protection atomically during reader admission. Surface expired or malformed positions as <xref:Orleans.Streams.DataNotAvailableException>. Treat throttling, iterator renewal, and transient transport errors according to the provider's retry contract.

Custom pooled caches use <xref:Orleans.Providers.Streams.Common.ICacheDataAdapter.Compare*> to position cursors against cached messages. The default implementation compares `SequenceNumber` and `EventIndex`, preserving the numeric ordering used by existing providers. Override it when the authoritative provider position is encoded in <xref:Orleans.Providers.Streams.Common.CachedMessage.Segment>; return an order consistent with the token produced by <xref:Orleans.Providers.Streams.Common.ICacheDataAdapter.GetSequenceToken*> so cache bounds, block selection, and cache-miss detection use the same position contract.

`AddPersistentStreams` leaves checkpointing to the adapter. The non-rewindable example acknowledges completed messages through its receiver and therefore has no independent checkpoint. For a partitioned stream transport, implement an <xref:Orleans.Streams.IStreamQueueCheckpointerFactory>, have the receiver or cache load and update the stream partition position, and register it as a named component with `ConfigureComponent`. Treat a requested cursor start as inclusive: selecting that position does not confirm its record. Persist only the earliest contiguous partition position which every subscription has delivered, intentionally filtered, or safely scanned as belonging to another stream. A no-op checkpointer is suitable only when replay position is deliberately disposable.

For a durable custom checkpoint backend, implement <xref:Orleans.Streams.IStreamCheckpointStore> and pass it to <xref:Orleans.Streams.StreamQueueCheckpointer> with <xref:Orleans.Streams.StreamQueueCheckpointerOptions>. Load the checkpointer before processing the partition. Each store update receives the expected backend version and returns the resulting <xref:Orleans.Streams.StreamCheckpointStoreState>, so a version conflict supplies the authoritative checkpoint and version for retry or reconciliation.

<xref:Orleans.Streams.StreamQueueCheckpointer> limits writes to the configured persistence interval, coalesces pending updates to the latest checkpoint, and flushes the latest position on demand. Set <xref:Orleans.Streams.StreamQueueCheckpointerOptions.CheckpointComparer> when checkpoint values have an ordering contract; the checkpointer then keeps progress monotonic across local updates and concurrent store writers.

## Register the provider

Register the transport client in dependency injection, then pass the factory's `Create` method to `AddPersistentStreams`. Configure queue count and cache capacity through the provider configurator.

:::code language="csharp" source="snippets/streaming/CustomQueueAdapter.cs" id="custom_queue_silo_registration":::

Register the same provider name and compatible mapping on Orleans clients which directly produce or consume streams:

:::code language="csharp" source="snippets/streaming/CustomQueueAdapter.cs" id="custom_queue_client_registration":::

Keep the provider name and partition count stable. Changing either can map an existing stream to a different queue and strand previously enqueued messages. Configure durable `PubSubStore` grain storage for explicit subscriptions in production; `PubSubStore` preserves subscription records independently from queue durability.

## Recover persisted token positions during an upgrade

Earlier inherited `CreateSequenceTokenForEvent` implementations returned an exact <xref:Orleans.Providers.Streams.Common.EventSequenceToken> or <xref:Orleans.Providers.Streams.Common.EventSequenceTokenV2>, including when the batch token had a custom subtype. Current factories preserve the concrete subtype and its metadata while changing the event index.

Orleans recovers these saved positions according to the provider's token contract:

| Token contract | Persisted positions supported during recovery |
| --- | --- |
| Generic V1/V2 and custom subclasses explicitly selecting the generic compatibility domain | Exact V1 and V2 positions compare with current tokens by sequence number and event index, with matching equality and hashes across the family. |
| Event Hubs V1/V2 and custom subclasses explicitly selecting the Event Hubs compatibility domain | Exact V1 positions from the earlier inherited factory are normalized within Event Hubs recovery comparisons. The sequence number and event index identify the position; current delivered tokens retain their Event Hubs offset and custom metadata. |
| Kinesis | Persisted Kinesis tokens retain the numeric shard offset as the authoritative position, followed by event index, across receiver restarts. |
| Redis | Persisted Redis tokens retain the entry ID, per-millisecond sequence number, and event index. |

Derived tokens are isolated by default, which preserves the identity contract of adapters compiled before this compatibility hook existed. A custom token which uses the complete generic numeric contract overrides `SequenceTokenCompatibilityDomain` to return `typeof(EventSequenceToken)`. Related token versions select the same stable domain type. A custom token which adds position identity keeps a distinct domain and implements equality, ordering, and hashing consistently for that domain.

Event Hubs, Kinesis, and Redis each retain their provider-specific public equality contract. Event Hubs recovery uses a sequence-only token with an empty offset for a legacy position whose factory omitted the offset; offset-bearing tokens come from the current provider data. Keep stream identity and partition mapping stable when replaying persisted positions.

## Validate failure behavior

Test the adapter against the real queue service, including:

1. batches containing multiple event types and request-context values;
1. empty reads, cancellation, transient errors, throttling, and shutdown;
1. producer, receiver, and silo failure before and after acknowledgement;
1. queue ownership moving between silos during membership changes;
1. duplicate delivery and consumer idempotency;
1. stable stream-to-partition mapping across restarts and upgrades;
1. sustained load beyond cache capacity to verify backpressure and queue retention;
1. quiet and busy streams sharing a partition, including restart after the quiet cursor scans unrelated records;
1. cancellation while partition ownership acquisition is blocked, followed by reassignment and late command completion; and
1. sequence-token equality, ordering, and hashing in both comparison directions.

Monitor queue depth and oldest-message age by partition, receive and acknowledgement latency, redelivery count, throttling, pulling-agent errors, and consumer delivery failures. Alert before retention or visibility limits can cause data loss or a redelivery storm.
