---
title: Stream with Amazon Kinesis
description: Configure Amazon Kinesis Data Streams for Orleans, including durable DynamoDB checkpoints.
ms.date: 09/03/2026
ms.topic: how-to
---

# Stream with Amazon Kinesis

The [`Microsoft.Orleans.Streaming.Kinesis`](https://www.nuget.org/packages/Microsoft.Orleans.Streaming.Kinesis) package connects Orleans persistent streams to [Amazon Kinesis Data Streams](https://docs.aws.amazon.com/streams/latest/dev/introduction.html). Each Kinesis shard is an Orleans queue, so the number of open shards bounds physical read parallelism. Kinesis retention determines how far a consumer can replay.

Create the Kinesis data stream before starting Orleans. The provider discovers its shards but doesn't create, delete, split, or merge the stream.

## Configure the silo

Install the package and register a named provider:

:::code language="csharp" source="../snippets/compiled/Streaming/KinesisSnippets.cs" id="kinesis_hosting_using":::
:::code language="csharp" source="../snippets/compiled/Streaming/KinesisSnippets.cs" id="configure_kinesis_silo":::

`PubSubStore` persists explicit Orleans stream subscriptions. The checkpoint table has a different purpose: it records the last delivered Kinesis sequence number for each shard.

Configure every Orleans client which publishes through the provider with the same provider name, stream name, and region:

:::code language="csharp" source="../snippets/compiled/Streaming/KinesisSnippets.cs" id="configure_kinesis_client":::

When explicit credentials aren't configured, the provider uses the [AWS SDK for .NET credential resolution chain](https://docs.aws.amazon.com/sdk-for-net/v4/developer-guide/creds-assign.html). In production, prefer workload credentials such as an IAM role. Set <xref:Orleans.Streaming.Kinesis.KinesisStreamOptions.Service> when using a custom Kinesis-compatible endpoint.

## Choose checkpoint storage

Kinesis shard iterators are temporary. Orleans therefore stores the last delivered sequence number outside Kinesis and uses it to resume after a restart or queue reassignment.

### DynamoDB table checkpoints

Call <xref:Orleans.Hosting.SiloKinesisStreamConfigurator.UseDynamoDBCheckpointer*> to store checkpoints directly in DynamoDB. The checkpointer:

- Uses one versioned item per Orleans service, provider, and Kinesis shard.
- Uses consistent reads and conditional writes to prevent a previous queue owner from overwriting a newer checkpoint.
- Creates an on-demand table by default. Set <xref:Orleans.Configuration.DynamoDBStreamQueueCheckpointerOptions.CreateIfNotExists> to `false` when infrastructure provisioning owns the table.
- Limits writes using <xref:Orleans.Configuration.DynamoDBStreamQueueCheckpointerOptions.PersistInterval>. A shorter interval reduces replay after failure but increases DynamoDB write traffic.

Set <xref:Orleans.Configuration.DynamoDBStreamQueueCheckpointerOptions.UseProvisionedThroughput>, <xref:Orleans.Configuration.DynamoDBStreamQueueCheckpointerOptions.ReadCapacityUnits>, and <xref:Orleans.Configuration.DynamoDBStreamQueueCheckpointerOptions.WriteCapacityUnits> when the table uses provisioned capacity.

### Grain checkpoints

If no checkpointer is selected, the provider uses Orleans grain-backed checkpoints. Checkpoint grains use `PubSubStore` by default, so that provider must be durable in production:

:::code language="csharp" source="../snippets/compiled/Streaming/KinesisSnippets.cs" id="kinesis_streams_using":::
:::code language="csharp" source="../snippets/compiled/Streaming/KinesisSnippets.cs" id="configure_grain_checkpoints":::

Both implementations preserve monotonic Kinesis sequence numbers and can replay a small number of already delivered records after an unclean shutdown. Consumers must tolerate duplicate delivery.

## Replay retained shard history

An explicit subscription can start or resume from a Kinesis sequence token while Kinesis retains the corresponding shard record. A start token includes its record; an acknowledged delivery token resumes after its record. The receiver opens an independent `AT_SEQUENCE_NUMBER` iterator, replays the shard in partition order, and renews an expired iterator from the last accepted historical position.

The receiver pins the oldest live-cache handoff position while replay is active. Historical records before that boundary are delivered first, then the subscription attaches to the live cursor before the historical reader is released. This transition preserves a contiguous partition scan. Delivery remains at least once across failures.

Configure retained-history capacity with <xref:Orleans.Hosting.SiloKinesisStreamConfigurator.ConfigureReplay*>. <xref:Orleans.Configuration.RecoverableStreamReplayOptions.MaxConcurrentReaders> bounds active shard iterators, <xref:Orleans.Configuration.RecoverableStreamReplayOptions.MaxPendingReaders> bounds queued admissions, and <xref:Orleans.Configuration.RecoverableStreamReplayOptions.CacheSize> bounds each replay fragment. Live and historical readers share the per-shard <xref:Orleans.Streaming.Kinesis.KinesisStreamOptions.GetRecordsInterval> gate so aggregate reads remain within Kinesis limits.

Kinesis tokens carry shard identity and arbitrary-precision shard sequence values. A token from another shard, an invalid sequence, or a position removed by Kinesis retention fails with <xref:Orleans.Streams.DataNotAvailableException>. Throughput throttling and iterator expiry retain their retry behavior.

## Operations and permissions

Grant the application only the Kinesis data-plane and DynamoDB table permissions required by its configuration. The Kinesis provider lists shards, obtains shard iterators, reads records, and writes records. A provider-managed checkpoint table also requires permissions to describe and create the table and to read and conditionally write checkpoint items.

Monitor Kinesis iterator age, read throttling, provisioned throughput, and retention together with the [Orleans streaming metrics](streaming-operations.md#observe-health). <xref:Orleans.Streaming.Kinesis.KinesisStreamOptions.GetRecordsInterval> defaults to the fastest interval allowed by Kinesis for each shard.

Live resharding isn't supported. If the shard topology changes while the provider is running, receivers stop rather than risk incorrect queue ownership. Restart the Orleans stream provider after splitting or merging shards.

For a complete configuration which uses DynamoDB for clustering, grain state, reminders, and Kinesis checkpoints, see the [AWS Kinesis and DynamoDB sample](https://github.com/dotnet/orleans/tree/main/samples/AWS/KinesisDynamoDB).
