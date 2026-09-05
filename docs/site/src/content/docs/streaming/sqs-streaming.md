---
title: Stream with Amazon SQS
description: Configure Amazon SQS streams for Orleans, including FIFO ordering, custom data adapters, and operational settings.
ms.date: 09/04/2026
ms.topic: how-to
---

# Stream with Amazon SQS

The [`Microsoft.Orleans.Streaming.SQS`](https://www.nuget.org/packages/Microsoft.Orleans.Streaming.SQS) package connects Orleans persistent streams to [Amazon Simple Queue Service](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/welcome.html). Orleans maps streams across a configurable set of SQS queues, creates a queue when its mapped partition is first used, receives batches through persistent-stream pulling agents, and deletes messages after successful delivery.

SQS streams provide [at-least-once delivery](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/standard-queues-at-least-once-delivery.html). A message becomes visible again when its [visibility timeout](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-visibility-timeout.html) expires before Orleans acknowledges it, so consumers must handle duplicates. The provider assigns receiver-local sequence tokens as messages arrive and doesn't support rewind to an earlier token.

## Configure a standard queue provider

Install the package and register a named provider on the silo:

:::code language="csharp" source="../snippets/compiled/Streaming/SqsSnippets.cs" id="configure_sqs_silo":::

`PubSubStore` persists explicit Orleans stream subscriptions. SQS retains event messages independently, while the configured grain storage retains subscription metadata.

Configure each Orleans client which publishes through the provider with the same provider name, connection, and partition count:

:::code language="csharp" source="../snippets/compiled/Streaming/SqsSnippets.cs" id="configure_sqs_client":::

The `Service` connection value accepts an AWS region such as `us-east-1` or an SQS-compatible endpoint such as `http://localhost:4566`. With a region, the provider uses the [AWS SDK for .NET credential resolution chain](https://docs.aws.amazon.com/sdk-for-net/v4/developer-guide/creds-assign.html) when the connection string contains no explicit credentials. Prefer workload credentials such as an IAM role. A protected connection string can supply `AccessKey`, `SecretKey`, and `SessionToken` when the deployment requires explicit temporary credentials.

## Preserve per-stream order with FIFO queues

Set <xref:Orleans.Configuration.SqsOptions.FifoQueue> to use SQS FIFO queues:

:::code language="csharp" source="../snippets/compiled/Streaming/SqsSnippets.cs" id="configure_sqs_fifo":::

The provider appends the `.fifo` suffix to its queue names and configures each new queue for FIFO throughput. It derives the SQS message group from the complete <xref:Orleans.Runtime.StreamId>, so SQS preserves publication order within one Orleans stream while processing different streams independently. Each publication receives a unique deduplication ID, preserving repeated events with identical payloads.

Received FIFO batches expose `SQSFIFOSequenceToken`. The token carries the SQS sequence number for same-stream ordering and a receiver-local sequence number for Orleans cache progress. FIFO delivery can still repeat a message after acknowledgement failure or visibility timeout, so processing remains idempotent.

Use the same `FifoQueue` value and partition count on silos and publishing clients. Changing either value creates a new queue topology and requires an explicit cutover which drains the previous queues.

## Use an application wire format

The default <xref:Orleans.Streaming.SQS.Streams.SQSDataAdapter> serializes Orleans batches into an Orleans-specific message body. Implement <xref:Orleans.Streaming.SQS.Streams.ISQSDataAdapter> when external applications need to produce or consume the messages, or when the application requires a versioned payload contract.

Register the same adapter on every silo and publishing client:

:::code language="csharp" source="../snippets/compiled/Streaming/SqsSnippets.cs" id="configure_sqs_data_adapter":::

The adapter converts between `Amazon.SQS.Model.Message` and <xref:Orleans.Streams.IBatchContainer>. On send, the provider uses the adapter's message body and application-defined message attributes, then supplies the queue URL and FIFO transport fields. On receive, the adapter gets the SQS message and a receiver-local sequence number.

List every application-defined attribute required by the decoder in <xref:Orleans.Configuration.SqsOptions.ReceiveMessageAttributes>. List required SQS system attributes in <xref:Orleans.Configuration.SqsOptions.ReceiveMessageSystemAttributes>; FIFO mode requests the SQS sequence number automatically. Keep stream identity, event ordering within a batch, schema versioning, and request-context handling stable across producers and consumers. See [Customize persistent-stream data formats](data-adapters.md) for wire-contract and rollout guidance.

## Tune queue and cache behavior

| Setting | Runtime behavior |
|---|---|
| <xref:Orleans.Hosting.SiloSqsStreamConfigurator.ConfigurePartitioning*> | Sets the physical SQS queue count. More queues increase pulling-agent parallelism and create more queues to operate. Keep the value identical on silos and clients. |
| <xref:Orleans.Hosting.SiloSqsStreamConfigurator.ConfigureCache*> | Sets the bounded in-memory cache size used by each pulling agent. Size it for event rate, consumer lag, and available silo memory. |
| <xref:Orleans.Configuration.SqsOptions.ReceiveWaitTimeSeconds> | Enables SQS long polling for receive requests and sets the queue default when Orleans creates the queue. Long polling reduces empty receives and request cost. |
| <xref:Orleans.Configuration.SqsOptions.VisibilityTimeoutSeconds> | Sets the queue visibility timeout when Orleans creates the queue. Choose a value longer than expected delivery and acknowledgement latency; expiration makes the message eligible for redelivery. |
| <xref:Orleans.Configuration.SqsOptions.ReceiveMessageAttributes> | Requests the application-defined attributes consumed by a custom data adapter. |
| <xref:Orleans.Configuration.SqsOptions.ReceiveMessageSystemAttributes> | Requests SQS system attributes consumed by the provider or application adapter. |

Queue-creation settings apply when the queue is absent. Manage changes to retention, visibility, encryption, access policy, and dead-letter redrive policy through SQS administration for existing queues.

Serialized Orleans batches must fit within the [SQS message quotas](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/quotas-messages.html). Bound batch size before messages approach the service limit, and include custom envelope and message-attribute overhead in that calculation.

## Permissions and operations

The runtime looks up and creates mapped queues, sends and receives messages, deletes delivered messages in batches, and resets pending-message visibility during receiver handoff so a replacement receiver can resume delivery promptly. Grant the application the corresponding `sqs:GetQueueUrl`, `sqs:CreateQueue`, `sqs:SendMessage`, `sqs:ReceiveMessage`, `sqs:DeleteMessage`, and `sqs:ChangeMessageVisibility` permissions for its queue-name scope. As shown in the [Amazon SQS API permissions reference](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-api-permissions-reference.html), the batch APIs use the respective `sqs:DeleteMessage` and `sqs:ChangeMessageVisibility` permissions. Administrative cleanup through <xref:OrleansAWSUtils.Streams.SQSStreamProviderUtils.DeleteAllUsedQueues*> also requires `sqs:DeleteQueue`.

Monitor SQS queue depth, age of the oldest message, receive count, empty receives, deletion failures, and dead-letter movement together with [Orleans streaming metrics](streaming-operations.md#observe-health). Rising oldest-message age indicates that pulling agents or consumers aren't keeping pace. Repeated receives indicate processing failures, acknowledgement failures, or a visibility timeout shorter than end-to-end delivery latency.
