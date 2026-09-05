---
title: Operate and tune Orleans streams
description: Apply backpressure, tune persistent providers, and observe Orleans streaming health.
ms.date: 08/17/2026
ms.topic: concept-article
---

# Operate and tune Orleans streams

Tune from measured lag, throughput, failures, and memory pressure. Provider defaults are starting points, not universal production settings.

## Backpressure

Persistent providers pull events into silo-side caches and deliver them to subscriptions. A consumer applies backpressure by not completing <xref:Orleans.Streams.IAsyncObserver`1.OnNextAsync*> until it has accepted responsibility for the item. Slow consumers can therefore increase retained queue data, cache pressure, and end-to-end lag.

Keep consumer turns bounded:

- Persist required state and complete promptly.
- Move independent long-running work behind an explicitly modeled handoff.
- Avoid synchronous blocking and unbounded parallel work.
- Scale by choosing enough provider queues or partitions and by distributing stream keys. One hot stream targeting one stateful grain remains limited by that grain's processing rate.

Backpressure on the consumer side doesn't imply producer completion. `OnNextAsync` on the producer reports provider acceptance, not that downstream consumers caught up.

### Protect a slow Event Hubs consumer

The Event Hubs provider maintains an independent cache cursor for each subscription, so a fast subscription can continue while another subscription falls behind. All cursors for an Event Hubs partition share the same silo-side cache, however. By default, the provider uses a weighted average of the pressure contributions from those cursors: contributions at or above the flow-control threshold receive three times the weight of lower-pressure contributions. Repeated contributions from faster subscriptions can still outweigh a small number of lagging subscriptions.

<xref:Orleans.Configuration.StreamCacheEvictionOptions.DataMinTimeInCache> and <xref:Orleans.Configuration.StreamCacheEvictionOptions.DataMaxAgeInCache> control time-based cache eviction; they don't guarantee that every subscription remains within the cache. If eviction advances past a lagging cursor, delivery reports `Item not found in cache`.

The slow-consuming monitor lets a single observed lagging cursor apply cache pressure instead of averaging that pressure with faster cursors:

:::code source="snippets/streaming/EventHubCachePressure.cs" id="event_hub_slow_consumer_pressure":::

The monitor starts calculating cursor pressure after the partition cache spans at least 10,000 Event Hubs sequence numbers. In this example, when Orleans reads the next cached item for a subscription whose cursor is more than 70% of the cache span behind the newest cached position, the provider stops new Event Hubs reads for at least 10 seconds. Tune both values from measured lag and processing time. The slow-consuming policy intentionally limits partition ingestion to protect the slowest observed subscription: cache misses are less likely, but end-to-end lag for every subscription can increase and backlog can move into Event Hubs. Ensure that Event Hubs retention can absorb that backlog and that sustained ingress doesn't exceed the slowest required subscription's capacity.

Pressure is sampled as Orleans advances subscriptions through cached items, so keep consumer turns bounded to keep detection current. First reduce CPU saturation and hot-grain bottlenecks. If workloads need independent throughput or retention policies, isolate them using separate Orleans stream providers and Event Hubs consumer groups instead of coupling them through one partition cache.

## Tune the pulling pipeline

Persistent providers expose common configuration through their stream configurators:

- <xref:Orleans.Configuration.StreamPullingAgentOptions.GetQueueMsgsTimerPeriod> trades polling frequency against latency and service calls.
- <xref:Orleans.Configuration.StreamPullingAgentOptions.BatchContainerBatchSize> controls how many queue batches are grouped for delivery.
- <xref:Orleans.Configuration.StreamPullingAgentOptions.MaxEventDeliveryTime> bounds delivery attempts before the configured failure handler is involved.
- <xref:Orleans.Configuration.SimpleQueueCacheOptions.CacheSize> controls item capacity for providers using the simple queue cache.

Provider-specific controls matter as much as common controls: <xref:Orleans.Configuration.AzureQueueOptions.QueueNames>, Event Hubs partitions and cache-pressure settings, Redis `ReadCount` and retention, NATS `BatchSize` and `PartitionCount`, and ADO.NET read size, checkpoints, and retention.

Change one bottleneck at a time. More queues can increase parallelism but also broker cost, polling load, cache memory, and rebalance work. Reducing polling delay can lower latency while increasing empty reads.

### Budget ADO.NET and Kinesis recovery caches

<xref:Orleans.Configuration.AdoNetStreamOptions.MaxCacheSizeBytes> and <xref:Orleans.Streaming.Kinesis.KinesisStreamOptions.MaxCacheSizeBytes> set an independent encoded-data budget for each partition or shard. Each defaults to 64 MiB. The budget counts retained cache segments: serialized payloads and their length framing, plus encoded Kinesis shard sequence numbers. <xref:Orleans.Configuration.SimpleQueueCacheOptions.CacheSize> also caps retained records, with a default of 4,096.

The receiver admits records in source order until either capacity is reached. It keeps the remainder of one fetched batch for subsequent admission and advances the source read offset only through admitted records. If the next record exceeds the available bytes, the cache applies backpressure until safe subscription delivery progress releases enough capacity. An empty cache admits one record larger than the configured budget; that record is delivered and released before further admission. Set producer record-size limits to bound this progress exception.

Plan silo memory from the number of locally owned partitions, including the highest partition count expected during reassignment. For example, 16 partitions with 64 MiB budgets permit 1 GiB of retained encoded segments before the oversized-record exception. Add headroom for:

- One fetched source batch per partition. ADO.NET reads at most <xref:Orleans.Configuration.AdoNetStreamOptions.MaxMessagesPerRead> records, defaulting to 1,000. Kinesis reads at most 1,000 records, also subject to the service response limit. These records can retain provider response buffers and decoded objects while waiting for admission.
- Buffer capacity and free-pool retention. Each partition uses 1 MiB pooled buffers; records larger than a pooled buffer use an exact-sized standalone buffer. Sequential packing leaves unused tails, and the oldest buffer can retain a delivered prefix while later records in that buffer remain cached. Pooled buffers are retained for reuse up to the pool's observed high-water mark.
- Cache metadata, source record wrappers, in-flight delivery copies, deserialized events, and the rest of the silo workload. Delivery batching and event object shape affect this headroom.

Use encoded record sizes together with producer record-size limits to choose the budget and read count. Lower read counts reduce staged-batch memory at the cost of more source requests. Monitor sustained cache pressure and provider backlog; provision retention to cover the slowest required consumer's recovery window. Safe delivery progress releases both record and byte capacity, and shutdown releases staged records and active cache-buffer ownership.

## Observe health

Export Orleans meters and correlate them with broker metrics and application event IDs. Useful Orleans instruments include:

| Signal | Instruments |
|---|---|
| Active topology | `orleans-streams-pubsub-producers`, `orleans-streams-pubsub-consumers`, `orleans-streams-persistent-stream-pulling-agents` |
| Throughput | `orleans-streams-persistent-stream-messages-read`, `orleans-streams-persistent-stream-messages-sent`, `orleans-streams-queue-messages-received` |
| Queue health | `orleans-streams-queue-read-failures`, `orleans-streams-queue-read-exceptions`, `orleans-streams-queue-oldest-message-enqueue-age` |
| Cache health | `orleans-streams-queue-cache-size`, `orleans-streams-queue-cache-length`, `orleans-streams-queue-cache-pressure`, `orleans-streams-queue-cache-under-pressure` |
| Memory | `orleans-streams-block-pool-total-memory`, `orleans-streams-block-pool-available-memory` |

Alert on sustained oldest-message age, cache pressure, read failures, repeated consumer exceptions, dead-letter growth, and a mismatch between expected and active subscription counts. Broker-side backlog and retention alarms remain necessary because Orleans can only report what its adapters observe.

For the components behind these signals, see [Orleans streams implementation](../implementation/streams-implementation/index.md).
