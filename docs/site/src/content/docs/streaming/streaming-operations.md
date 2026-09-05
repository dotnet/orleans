---
title: Operate and tune Orleans streams
description: Apply backpressure, tune persistent providers, and observe Orleans streaming health.
ms.date: 09/01/2026
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

### Bound Event Hubs cache memory

The Event Hubs provider applies one active cache memory watermark across all partitions owned by a provider instance. The default <xref:Orleans.Configuration.EventHubStreamCacheMemoryOptions.MaxActiveCacheMemory> is 512 MiB. It includes active payload buffers and cached-message metadata. When usage reaches the watermark, Orleans pauses new Event Hubs reads. Partition caches without active subscriptions release eligible oldest buffers until provider usage falls below the watermark. Caches with active subscriptions retain their delivery boundary, so memory pressure pauses ingestion instead of advancing past a slow or in-flight consumer.

The built-in adaptive buffer pool retains up to 64 MiB of idle payload buffers per provider by default through <xref:Orleans.Configuration.EventHubStreamCacheMemoryOptions.MaxBufferPoolMemory>. Buffers above that limit are released when they become idle. A custom buffer pool controls its own allocation and retention policy. Configure both values with <xref:Orleans.Hosting.SiloEventHubStreamConfiguratorExtensions.ConfigureCacheMemory*>.

The active watermark is a flow-control threshold rather than a hard process-memory ceiling. Reads already in flight complete so that dequeued events remain available for ordered delivery. Temporary overshoot therefore scales with concurrent partition reads, received batch sizes, event payload sizes, and cached-message metadata growth. Size the watermark with headroom for one in-flight read per concurrently active partition, and keep Event Hubs retention long enough to absorb the backlog while reads are paused.

Increase the active watermark when measured cache pressure repeatedly pauses healthy consumers and the silo has sufficient memory headroom. Reduce it to reserve memory for grains and other providers. Increase idle retention when allocation churn is measurable after bursts; reduce it when many providers or silos retain unused cache buffers. Monitor `orleans-streams-queue-cache-under-pressure`, `orleans-streams-queue-cache-size`, `orleans-streams-block-pool-total-memory`, broker backlog, and silo process memory together.

## Tune the pulling pipeline

Persistent providers expose common configuration through their stream configurators:

- <xref:Orleans.Configuration.StreamPullingAgentOptions.GetQueueMsgsTimerPeriod> trades polling frequency against latency and service calls.
- <xref:Orleans.Configuration.StreamPullingAgentOptions.BatchContainerBatchSize> controls how many queue batches are grouped for delivery.
- <xref:Orleans.Configuration.StreamPullingAgentOptions.MaxEventDeliveryTime> bounds delivery attempts before the configured failure handler is involved.
- <xref:Orleans.Configuration.SimpleQueueCacheOptions.CacheSize> controls item capacity for providers using the simple queue cache.

Provider-specific controls matter as much as common controls: <xref:Orleans.Configuration.AzureQueueOptions.QueueNames>, Event Hubs partitions and cache-pressure settings, Redis `ReadCount` and retention, NATS `BatchSize` and `PartitionCount`, and ADO.NET visibility, expiry, and dead-letter settings.

Change one bottleneck at a time. More queues can increase parallelism but also broker cost, polling load, cache memory, and rebalance work. Reducing polling delay can lower latency while increasing empty reads.

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
