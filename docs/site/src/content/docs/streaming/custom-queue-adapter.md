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

Receipt-based adapters acknowledge completed messages through their receiver. For a retained-log transport, implement <xref:Orleans.Streams.ICheckpointingQueueCache> and the progress and recovery contracts below. Register an <xref:Orleans.Streams.IStreamQueueCheckpointerFactory> as a named component with `ConfigureComponent`, and load its per-partition position before reading.

For a durable custom checkpoint backend, implement <xref:Orleans.Streams.IStreamCheckpointStore> and pass it to <xref:Orleans.Streams.StreamQueueCheckpointer> with <xref:Orleans.Streams.StreamQueueCheckpointerOptions>. Load the checkpointer before processing the partition. Each store update receives the expected backend version and returns the resulting <xref:Orleans.Streams.StreamCheckpointStoreState>, so a version conflict supplies the authoritative checkpoint and version for retry or reconciliation.

<xref:Orleans.Streams.StreamQueueCheckpointer> limits writes to the configured persistence interval, coalesces pending updates to the latest checkpoint, and flushes the latest position on demand. Set <xref:Orleans.Streams.StreamQueueCheckpointerOptions.CheckpointComparer> when checkpoint values have an ordering contract; the checkpointer then keeps progress monotonic across local updates and concurrent store writers.

## Migrate a custom checkpointing provider

Checkpointing providers use one certified partition prefix. The former `IQueueCache.UpdateDeliveryProgress` callback moves to <xref:Orleans.Streams.ICheckpointingQueueCache.UpdateDeliveryProgress*>. The callback receives a non-null whole-record certificate. Pending or unknown progress withholds the callback; when there are no subscriptions, the certificate is the last fully accounted read boundary.

Update the provider at these boundaries:

| Component | Required implementation |
| --- | --- |
| Queue cache | Implement <xref:Orleans.Streams.ICheckpointingQueueCache>. Make cache admission atomic for recoverable failures: retrying the same returned read must preserve the previously admitted prefix and admit each record once. Integrated receivers can stage and admit bounded prefixes before handing notifications to the agent. |
| Cache cursor | Implement <xref:Orleans.Streams.IQueueCacheCursorProgress>, including through wrappers. Publish a stable, owned safe token for the contiguous scanned and acknowledged prefix. Retain the first pending matching record until the complete selected batch is acknowledged or filtered. Failed materialization preserves selection; failed delivery rewinds the pending range. |
| Event-level resume | Implement <xref:Orleans.Streams.IQueueCacheBatchContainerFilter> when a token can identify an event inside a provider record. Preserve event indexes, provider offset metadata, and request context when slicing. The cache data adapter compares provider records consistently; the cursor delivers the remaining events before certifying the whole record. |
| Receiver | Implement <xref:Orleans.Streams.IQueueAdapterReceiverReadRecovery>. Successful recovery restores every record after the receiver's last successful handoff, including cached records whose notifications were not returned. Keep the provider resume position and staged suffix inside the current ownership run. Retention loss, ownership loss, and canceled recovery remain explicit failures. |
| Checkpoint store | Persist only the certified whole-record position. Preserve the provider's epoch or version fencing and serialized checkpoint writes. A delivery token inside a record becomes a checkpoint only after the rest of that record is accounted for. |
| Event Hubs cache and eviction | Implement <xref:Orleans.Streaming.EventHubs.IEventHubQueueCache.UpdateDeliveryProgress*> and expose progress from the returned raw cursors. Scope eviction permission to the supplied certificate. Purge policies use <xref:Orleans.Providers.Streams.Common.IPurgeObservable.TryRemoveOldestMessage*> and count removal only after it returns true. Repeated <xref:Orleans.Providers.Streams.Common.IEvictionStrategy.OnBlockAllocated*> notifications for the same owned buffer are idempotent. Metadata removal and shared-buffer reclamation stay in the existing eviction lifecycle. |

The runtime validates checkpoint receiver capabilities before initialization or source reads, and cursor capabilities when a cursor is acquired. An unmarked cache with the exact former public delivery-progress callback, including an inherited callback, fails configuration with migration guidance. Explicit implementations of the removed interface member require recompilation; old binaries can fail interface loading. Ordinary receipt-based caches continue through their established receive and acknowledgement path.

The pulling agent owns a successfully returned batch until admission and registration accounting finish. It serializes reads with read recovery, retains failed-registration pins for retry, and drains accepted work before releasing a receiver. A completed recovery repairs only its own read obligation. Subscription replay and handshake generations retain independent ownership.

Validate the migration with a held delivery while other streams advance, a failed selected batch followed by retry, a throwing materializer, a failed read followed by an empty certified recovery, partial admission attempts, registration retries, and shutdown during recovery. Verify that a later acknowledgment cannot cross an unresolved gap, that idle subscriptions advance through unrelated records, and that a bare purge call cannot reuse an earlier certificate after a subscription repositions.

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
1. sustained load beyond cache capacity to verify backpressure and queue retention; and
1. sequence-token equality, ordering, and hashing in both comparison directions.

Monitor queue depth and oldest-message age by partition, receive and acknowledgement latency, redelivery count, throttling, pulling-agent errors, and consumer delivery failures. Alert before retention or visibility limits can cause data loss or a redelivery storm.
