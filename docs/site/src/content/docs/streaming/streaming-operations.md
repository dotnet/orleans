---
title: Operate and tune Orleans streams
description: Apply backpressure, tune persistent providers, and observe Orleans streaming health.
ms.date: 09/10/2026
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

Provider-specific controls matter as much as common controls: <xref:Orleans.Configuration.AzureQueueOptions.QueueNames>, Event Hubs partitions and cache-pressure settings, Redis `ReadCount` and retention, NATS `BatchSize` and `PartitionCount`, and ADO.NET read size, checkpoint, retention, and replay-lease settings.

Change one bottleneck at a time. More queues can increase parallelism but also broker cost, polling load, cache memory, and rebalance work. Reducing polling delay can lower latency while increasing empty reads.

### Tune memory stream dequeue batches

For each named memory stream provider, <xref:Orleans.Configuration.MemoryStreamCacheOptions.MaxAddCount> bounds the number of queue records requested in a single dequeue operation. The default is `100`, and options validation requires a value greater than zero. Configure it on the silo using <xref:Orleans.Hosting.MemoryStreamConfiguratorExtensions.ConfigureCache*> in the provider's `AddMemoryStreams` callback. For configuration-based provider registration, set `MaxAddCount` in the named memory provider's configuration section.

Each record contains one published batch of events. A value such as `25` reduces the number of records combined into each queue-grain response, at the cost of more dequeue calls for the same throughput. Larger values amortize call overhead across more records and can increase serialization work, response size, and memory usage per call.
The bound is measured in records. The aggregate byte size depends on the serialized payloads in those records, including all events in each published batch. Size producer batches and payloads so that a complete dequeue response fits the configured Orleans message-body limit. The queue grain removes records before its response is serialized; an oversized response can therefore lose those records when serialization fails. Tune the count alongside measured response sizes and queue lag.
The bound is measured in records. The aggregate byte size depends on the serialized payloads in those records, including all events in each published batch. Size producer batches and payloads so that a complete dequeue response fits the configured Orleans message-body limit. The queue grain removes records before its response is serialized; an oversized response can therefore lose those records when serialization fails. Tune the count alongside measured response sizes and queue lag.

## Diagnose retained-history replay

Kinesis and ADO.NET retained replay use bounded independent readers. Correlate subscription errors with provider health and the configured <xref:Orleans.Configuration.RecoverableStreamReplayOptions>:

| Symptom | Interpretation and action |
|---|---|
| <xref:Orleans.Streams.DataNotAvailableException> at subscription attachment | The token has an invalid provider or partition identity, retention removed its record, or retained history ended before the pinned live-cache boundary. Orleans keeps the failure visible instead of silently starting at live data. Select a valid retained token or explicitly choose a newer start. |
| <xref:Orleans.Streams.TransientStreamReplayException> | Historical reader creation, reading, or lease renewal failed transiently. The pulling agent reports the failure and retries from its latest safe partition position with delivery backoff. Investigate Kinesis throttling and credentials or ADO.NET connectivity, latency, locks, and pool pressure. |
| `retained-history replay admission queue reached its configured limit` | `MaxConcurrentReaders` are occupied and `MaxPendingReaders` waiting cursors are already admitted. Reduce simultaneous replay or raise bounded capacity only after validating provider read limits and memory. |
| Replay remains active without delivery | A provider reader can be at a temporary tail, or its item-count fragment cache can be full behind a slow cursor. Inspect downstream processing time, replay cache sizing, provider read latency, and the retained window. |
| Shutdown reports reader cleanup, checkpoint flush, or provider shutdown errors | Receiver shutdown attempts every cleanup stage and preserves failures. Keep the full exception or aggregate and allow sufficient <xref:Orleans.Configuration.StreamPullingAgentOptions.InitQueueTimeout> for provider cleanup. |

Correlate subscription errors, provider logs and metrics, queue-cache pressure, and downstream lag to diagnose replay demand and progress. For the replay state machine and option defaults, see [Replay retained persistent-stream history](retained-history-replay.md).

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
