---
title: Orleans stream providers
description: Compare built-in Orleans stream providers by durability, rewindability, status, and prerequisites.
ms.date: 09/03/2026
ms.topic: concept-article
---

# Orleans stream providers

A stream provider connects the Orleans streaming API to a transport and defines its runtime semantics. Select a provider from required durability, replay, throughput, hosting environment, and operational ownership. Provider behavior still depends on backing-service configuration such as retention, replication, and visibility timeouts.

## Provider matrix

| Provider | Package | Status | External event durability | Rewindable | External prerequisites |
|---|---|---|---|---|---|
| Memory | [`Microsoft.Orleans.Streaming`](https://www.nuget.org/packages/Microsoft.Orleans.Streaming) | Stable | No; silo memory only | Yes, within the transient in-memory cache | None |
| Azure Queue Storage | [`Microsoft.Orleans.Streaming.AzureStorage`](https://www.nuget.org/packages/Microsoft.Orleans.Streaming.AzureStorage) | Stable | Yes, in Azure Storage queues | No | Azure Storage account or Azurite; credentials and a stable Orleans service ID |
| Azure Event Hubs | [`Microsoft.Orleans.Streaming.EventHubs`](https://www.nuget.org/packages/Microsoft.Orleans.Streaming.EventHubs) | Stable | Yes, within Event Hubs retention | Yes, within the live Orleans cache | Event Hubs namespace, hub, consumer group, and checkpoint storage |
| Amazon Kinesis | [`Microsoft.Orleans.Streaming.Kinesis`](https://www.nuget.org/packages/Microsoft.Orleans.Streaming.Kinesis) | Stable | Yes, within Kinesis retention | Yes, within Kinesis retention | Kinesis data stream, AWS credentials, region, and durable checkpoint storage |
| Amazon SQS | [`Microsoft.Orleans.Streaming.SQS`](https://www.nuget.org/packages/Microsoft.Orleans.Streaming.SQS) | Stable | Yes, within SQS retention | No | AWS account, queue permissions, region/endpoint configuration |
| ADO.NET partitioned stream | [`Microsoft.Orleans.Streaming.AdoNet`](https://www.nuget.org/packages/Microsoft.Orleans.Streaming.AdoNet) | **Alpha** | Yes, in relational stream partitions | Yes, within configured retention | Supported database, ADO.NET driver, and matching Orleans streaming SQL schema |
| NATS JetStream | [`Microsoft.Orleans.Streaming.NATS`](https://www.nuget.org/packages/Microsoft.Orleans.Streaming.NATS) | **Alpha** | Configurable; file storage is the default | No | NATS server with JetStream and sufficient storage; subject/stream administration |
| Redis Streams | [`Microsoft.Orleans.Streaming.Redis`](https://www.nuget.org/packages/Microsoft.Orleans.Streaming.Redis) | **Alpha** | Configurable through Redis persistence and stream retention | Yes, within the live Orleans cache | Redis deployment, persistence/HA policy, and retention sizing |

Alpha packages have an `alpha.1` version suffix. Treat their APIs and operational behavior as prerelease, validate failure modes under load, and pin versions deliberately.

Kafka and Azure Service Bus aren't built-in Orleans stream providers. Integrate them through application code or a custom persistent-stream queue adapter rather than configuring a nonexistent built-in provider.

## Memory streams

Register memory streams with <xref:Orleans.Hosting.SiloBuilderMemoryStreamExtensions.AddMemoryStreams*>. They use silo memory for queues and cache, so events don't survive cluster loss. Rewind works only while the relevant event remains in the live in-memory cache. Use this provider for local development, tests, and workloads where loss is explicitly acceptable.

## Azure Queue Storage

<a id="azure-queue-aq-stream-provider"></a>

Register Azure Queue streams with <xref:Orleans.Hosting.SiloBuilderExtensions.AddAzureQueueStreams*>. The provider uses multiple [Azure Queue Storage](https://learn.microsoft.com/azure/storage/queues/storage-queues-introduction) queues and persistent-stream pulling agents. It isn't rewindable, and Azure Queue retries can produce duplicates or reorder delivery after failures.

Configure the current <xref:Azure.Storage.Queues.QueueServiceClient> directly on <xref:Orleans.Configuration.AzureQueueOptions>. When <xref:Orleans.Configuration.AzureQueueOptions.QueueNames> is unset, Orleans generates names from the Orleans service ID, provider name, and queue ID. Keep the service ID and provider name stable across restarts. Set queue names explicitly only when you need to manage an existing queue topology, and keep those names unique across clusters that share a storage account.

### Managed identity

:::code language="csharp" source="snippets/streaming/Configuration.cs" id="azure_queue_managed_identity":::

### Connection string

:::code language="csharp" source="snippets/streaming/Configuration.cs" id="azure_queue_connection_string":::

The examples use durable Azure Table Storage for `PubSubStore`; queue durability alone doesn't preserve explicit subscription records.

## Azure Event Hubs

<a id="azure-event-hub-stream-provider"></a>

Register [Azure Event Hubs](https://learn.microsoft.com/azure/event-hubs/event-hubs-about) with <xref:Orleans.Hosting.SiloBuilderExtensions.AddEventHubStreams*>. Configure a consumer group dedicated to the Orleans application and durable checkpoint storage. Partition count bounds physical read parallelism, and Event Hubs retention bounds receiver recovery after restart or reassignment.

Explicit subscriptions can resume from Event Hubs sequence tokens while their records remain in the pulling agent's live cache. New subscriptions can also [select a cache-relative start position](subscription-start-positions.md). Cache-local rewind keeps the Event Hubs partition receiver and checkpoint at their current positions.

The Event Hubs provider supports a custom data adapter for provider-specific wire formats. See [Integrate external stream producers and consumers](external-streams.md) when a non-Orleans application must publish to or consume from the same Event Hub.

## Amazon Kinesis

Register [Amazon Kinesis Data Streams](https://docs.aws.amazon.com/streams/latest/dev/introduction.html) with <xref:Orleans.Hosting.SiloBuilderExtensions.AddKinesisStreams*>. Kinesis retains events independently of Orleans, and the provider persists each shard's earliest safe scan/delivery position so that delivery can resume after shutdown or queue reassignment. An explicit subscription can also start or resume from a retained Kinesis token after its record leaves the live cache. The receiver opens an independent shard reader, replays partition records in order, and attaches the subscription to the live cache at a pinned boundary. Kinesis sequence tokens identify their shard and compare using the arbitrary-precision shard sequence and event index. See [Stream with Amazon Kinesis](kinesis-streaming.md) for configuration, checkpoint choices, and operational constraints.

## Amazon SQS

Register [Amazon SQS](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/welcome.html) with <xref:Orleans.Hosting.SiloBuilderExtensions.AddSqsStreams*>. Standard queues provide at-least-once delivery, while FIFO queues preserve ordering within each Orleans stream. SQS redelivers after the [visibility timeout](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-visibility-timeout.html) when processing isn't acknowledged. See [Stream with Amazon SQS](sqs-streaming.md) for standard and FIFO configuration, custom data adapters, permissions, and operational guidance.

## ADO.NET partitioned stream provider (alpha)

Register the [ADO.NET](https://learn.microsoft.com/dotnet/framework/data/adonet/ado-net-overview) partitioned stream provider with <xref:Orleans.Hosting.SiloBuilderAdoNetStreamExtensions.AddAdoNetStreams*>. Install the matching database driver and apply the SQL Server, PostgreSQL, or MySQL streaming schema shipped in the package source. Each queue maps to an immutable, ordered stream partition with a durable, ownership-fenced checkpoint. The stream partition pipeline resumes strictly after the earliest safe scan/delivery checkpoint, redelivering uncheckpointed records after recovery. An explicit subscription token can open an independent retained-history reader. Admission atomically validates the retained lower bound and creates an epoch-fenced database lease before cleanup can proceed. Active leases protect history across silos until safe replay progress advances or the lease expires. <xref:Orleans.Configuration.AdoNetStreamOptions.ReplayLeaseDuration*> and <xref:Orleans.Configuration.AdoNetStreamOptions.ReplayLeaseRenewalInterval*> control that protection.

<xref:Orleans.Configuration.AdoNetStreamOptions.RetentionPeriod*> retains checkpointed records for one day by default before cleanup. <xref:Orleans.Configuration.AdoNetStreamOptions.MaximumRetentionPeriod*> imposes an optional hard storage ceiling and can terminate a slow replay with a diagnosed retention gap. Retention and cleanup intervals round fractional seconds upward. Cleanup is bounded by <xref:Orleans.Configuration.AdoNetStreamOptions.CleanupBatchSize*> and deletes a contiguous eligible prefix. <xref:Orleans.Configuration.AdoNetStreamOptions.FaultOnDeliveryFailure*> can fault one failing subscription while preserving the multicast partition record.

The current alpha schema is version 3, and upgrades replace the streaming schema as one unit. Before upgrading, stop producers and consumers, drop `OrleansStreamMessage`, `OrleansStreamReplayLease`, `OrleansStreamPartition`, former dead-letter/control/sequence objects, streaming routines, and their `OrleansQuery` rows, and then apply the current streaming script. Preserve required payloads externally before replacing alpha tables.

## NATS JetStream streaming (alpha)

Register [NATS JetStream](https://docs.nats.io/nats-concepts/jetstream) with `AddNatsStreams`. The provider creates or uses a JetStream stream and deterministic subject partitions. File-backed storage is the default; memory-backed JetStream storage is optional and not durable across server loss. Changes to `NatsOptions.PartitionCount` require corresponding server-side stream updates. The provider isn't rewindable.

## Redis Streams streaming (alpha)

Register [Redis Streams](https://redis.io/docs/latest/develop/data-types/streams/) with `AddRedisStreams`. The provider stores events and checkpoints in Redis, and explicit subscriptions can resume from tokens while their records remain in the live Orleans cache. Redis durability depends on its [persistence](https://redis.io/docs/latest/operate/oss_and_stack/management/persistence/) and replication configuration. `RedisStreamingOptions.MaxStreamLength` can bound transport retention; without it, stream length is unbounded, so capacity planning is required.

## Custom adapters

<a id="queue-adapters"></a>

A [persistent-stream data adapter](data-adapters.md) customizes the wire format used by Azure Queue Storage, Azure Event Hubs, or Amazon SQS while retaining that provider's transport, partitioning, acknowledgement, cache, and recovery behavior.

<xref:Orleans.Providers.Streams.Common.PersistentStreamProvider> hosts providers built on <xref:Orleans.Streams.IQueueAdapter>. A custom queue adapter supplies enqueue/dequeue behavior, queue mapping, rewindability, and failure handling while Orleans supplies pulling agents, subscription routing, and caches. See [Write a custom persistent-stream queue adapter](custom-queue-adapter.md) for an implementation guide and [stream implementation architecture](../implementation/streams-implementation/index.md) for the runtime design.
