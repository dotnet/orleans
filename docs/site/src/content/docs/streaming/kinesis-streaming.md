---
title: Stream with Amazon Kinesis
description: Configure Amazon Kinesis Data Streams for Orleans, including durable DynamoDB checkpoints.
ms.date: 08/25/2026
ms.topic: how-to
---

# Stream with Amazon Kinesis

The [`Microsoft.Orleans.Streaming.Kinesis`](https://www.nuget.org/packages/Microsoft.Orleans.Streaming.Kinesis) package connects Orleans persistent streams to [Amazon Kinesis Data Streams](https://docs.aws.amazon.com/streams/latest/dev/introduction.html). Each Kinesis shard is an Orleans queue, so the number of open shards bounds physical read parallelism. Kinesis retention determines how far a consumer can replay.

Provision the Kinesis data stream before starting Orleans. The provider maps every non-expired shard returned by Kinesis to an Orleans queue. Infrastructure automation owns stream creation, retention, encryption, and shard-count changes.

## Configure the silo

Install the package and register a named provider:

:::code language="csharp" source="../snippets/compiled/Streaming/KinesisSnippets.cs" id="kinesis_hosting_using":::
:::code language="csharp" source="../snippets/compiled/Streaming/KinesisSnippets.cs" id="configure_kinesis_silo":::

`PubSubStore` persists explicit Orleans stream subscriptions. The checkpoint table has a different purpose: it records the last delivered Kinesis sequence number for each shard.

Configure every Orleans client which publishes through the provider with the same provider name, stream name, and region:

:::code language="csharp" source="../snippets/compiled/Streaming/KinesisSnippets.cs" id="configure_kinesis_client":::

When explicit credentials aren't configured, the provider uses the [AWS SDK for .NET credential resolution chain](https://docs.aws.amazon.com/sdk-for-net/v4/developer-guide/creds-assign.html). In production, prefer workload credentials such as an IAM role. Set <xref:Orleans.Streaming.Kinesis.KinesisStreamOptions.Service> when using a custom Kinesis-compatible endpoint.

## Configure Kinesis with Aspire

The Aspire application model uses the AWS-supported [`Aspire.Hosting.AWS`](https://www.nuget.org/packages/Aspire.Hosting.AWS) integration to configure AWS SDK for .NET v4 and provision the complete durable topology through AWS CDK. The CDK stack owns the Kinesis stream, DynamoDB `PubSubStore`, and DynamoDB checkpoint table.

Define stable physical names, stream capacity, retention, table schemas, and deployment identity once:

:::code language="csharp" source="../host/snippets/aspire/AppHost/AppHostExamples.cs" id="kinesis_topology":::

Create the AWS SDK configuration and CDK stack, then feed the same topology into the constructs and Orleans:

:::code language="csharp" source="../host/snippets/aspire/AppHost/AppHostExamples.cs" id="kinesis_streaming_apphost":::

The provider configuration references the AWS SDK metadata and emits the Kinesis resource identity plus DynamoDB checkpoint selection:

:::code language="csharp" source="../host/snippets/aspire/AppHost/AppHostExamples.cs" id="kinesis_provider_configuration":::

`WithReference(stream)` maps the CDK `StreamArn` output to `AWS:Resources:orders-stream:StreamArn`. The provider validates the ARN, while the shared topology supplies the effective stream name and region. The DynamoDB references map each `TableName` output through its service key. Provider-local configuration carries the checkpoint region and `PubSubStore` service ID in every deployment mode. Run-mode environment contains AWS profile and region metadata when the AWS SDK configuration selects them, and the AWS SDK credential chain supplies workload credentials.

The `PubSubStore` table uses the `GrainReference` partition key and `GrainType` sort key required by DynamoDB grain storage. The checkpoint table uses `CheckpointNamespace` and `Partition`. CDK configures both tables for on-demand billing, and Orleans receives `CreateIfNotExists=false`, `UpdateIfExists=false`, and `UseProvisionedThroughput=false` for the infrastructure-owned grain storage. The direct checkpoint provider receives `CreateIfNotExists=false` and `UseProvisionedThroughput=false`.

The silo waits for the CDK stack and activates generated configuration:

:::code language="csharp" source="../host/snippets/aspire/Silo/SiloProgram.cs" id="kinesis_streaming_silo":::

The publishing client waits for both the stack and silo, then activates the same stream provider name and physical stream:

:::code language="csharp" source="../host/snippets/aspire/Client/ClientProgram.cs" id="kinesis_streaming_client":::

The CDK stack deploys through CloudFormation in the account and region selected by the AWS SDK configuration. Grant the AppHost identity permission to create and update the stack. Bootstrap the environment when constructs use CDK assets. See [Provisioning application resources with AWS CDK](https://github.com/aws/integrations-on-dotnet-aspire-for-aws/blob/main/src/Aspire.Hosting.AWS/README.md#provisioning-application-resources-with-aws-cdk) for the integration contract.

Keep `ClusterId`, `ServiceId`, provider name, stream name, and table names stable across rolling deployments. `PubSubStore` preserves explicit subscriptions, and the checkpoint table preserves the last delivered sequence number for every shard. A deliberate cutover can introduce new names, run both topologies during migration, drain consumers, and then retire the previous resources.

For local Kinesis-compatible and DynamoDB services, configure `Service`, `Checkpoint:Service`, or the AWS SDK service-specific `AWS_ENDPOINT_URL_KINESIS` and `AWS_ENDPOINT_URL_DYNAMODB` variables. Keep the same stream and table identities so local activation exercises the deployed configuration contract.

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

## Operations and permissions

Grant the application only the Kinesis data-plane and DynamoDB table permissions required by its configuration. The Kinesis provider lists shards, obtains shard iterators, reads records, and writes records. A provider-managed checkpoint table also requires permissions to describe and create the table and to read and conditionally write checkpoint items.

Monitor Kinesis iterator age, read throttling, provisioned throughput, and retention together with the [Orleans streaming metrics](streaming-operations.md#observe-health). <xref:Orleans.Streaming.Kinesis.KinesisStreamOptions.GetRecordsInterval> defaults to the fastest interval allowed by Kinesis for each shard.

When the shard topology changes, receivers stop after detecting the new topology so ownership remains unambiguous. Restart the Orleans stream provider after the CDK or operational shard update completes. Increasing shard count changes read parallelism while the stable stream name, subscriptions, and checkpoints continue to identify the same durable stream.

For a complete configuration which uses DynamoDB for clustering, grain state, reminders, and Kinesis checkpoints, see the [AWS Kinesis and DynamoDB sample](https://github.com/dotnet/orleans/tree/main/samples/AWS/KinesisDynamoDB).
