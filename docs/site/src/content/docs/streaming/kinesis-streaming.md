---
title: Stream with Amazon Kinesis
description: Configure Amazon Kinesis Data Streams for Orleans, including durable DynamoDB checkpoints.
ms.date: 08/07/2026
ms.topic: how-to
---

# Stream with Amazon Kinesis

The [`Microsoft.Orleans.Streaming.Kinesis`](https://www.nuget.org/packages/Microsoft.Orleans.Streaming.Kinesis) package connects Orleans persistent streams to [Amazon Kinesis Data Streams](https://docs.aws.amazon.com/streams/latest/dev/introduction.html). Each Kinesis shard is an Orleans queue, so the number of open shards bounds physical read parallelism. Kinesis retention determines how far a consumer can replay.

Create the Kinesis data stream before starting Orleans. The provider discovers its shards but doesn't create, delete, split, or merge the stream.

## Configure the silo

Install the package and register a named provider:

```csharp
using Orleans.Hosting;

siloBuilder
    .AddDynamoDBGrainStorage("PubSubStore", options =>
    {
        options.Service = "us-east-1";
        options.ServiceId = "orders";
        options.TableName = "OrdersPubSub";
        options.UseProvisionedThroughput = false;
    })
    .AddKinesisStreams("Orders", stream =>
    {
        stream.ConfigureKinesis(options =>
        {
            options.StreamName = "orders";
            options.Region = "us-east-1";
        });

        stream.UseDynamoDBCheckpointer(options =>
        {
            options.Service = "us-east-1";
            options.TableName = "OrdersStreamCheckpoints";
            options.PersistInterval = TimeSpan.FromSeconds(30);
        });
    });
```

`PubSubStore` persists explicit Orleans stream subscriptions. The checkpoint table has a different purpose: it records the last delivered Kinesis sequence number for each shard.

Configure every Orleans client which publishes through the provider with the same provider name, stream name, and region:

```csharp
clientBuilder.AddKinesisStreams("Orders", options =>
{
    options.StreamName = "orders";
    options.Region = "us-east-1";
});
```

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

```csharp
using Orleans.Streams;

siloBuilder.AddKinesisStreams("Orders", stream =>
{
    stream.ConfigureKinesis(options =>
    {
        options.StreamName = "orders";
        options.Region = "us-east-1";
    });

    stream.UseGrainCheckpointer(options =>
    {
        options.StorageProviderName = "PubSubStore";
        options.CheckpointComparer = StreamCheckpointComparers.Numeric;
        options.PersistInterval = TimeSpan.FromSeconds(30);
    });
});
```

Both implementations preserve monotonic Kinesis sequence numbers and can replay a small number of already delivered records after an unclean shutdown. Consumers must tolerate duplicate delivery.

## Operations and permissions

Grant the application only the Kinesis data-plane and DynamoDB table permissions required by its configuration. The Kinesis provider lists shards, obtains shard iterators, reads records, and writes records. A provider-managed checkpoint table also requires permissions to describe and create the table and to read and conditionally write checkpoint items.

Monitor Kinesis iterator age, read throttling, provisioned throughput, and retention together with the [Orleans streaming metrics](streaming-operations.md#observe-health). <xref:Orleans.Streaming.Kinesis.KinesisStreamOptions.GetRecordsInterval> defaults to the fastest interval allowed by Kinesis for each shard.

Live resharding isn't supported. If the shard topology changes while the provider is running, receivers stop rather than risk incorrect queue ownership. Restart the Orleans stream provider after splitting or merging shards.

For a complete configuration which uses DynamoDB for clustering, grain state, reminders, and Kinesis checkpoints, see the [AWS Kinesis and DynamoDB sample](https://github.com/dotnet/orleans/tree/main/samples/AWS/KinesisDynamoDB).
