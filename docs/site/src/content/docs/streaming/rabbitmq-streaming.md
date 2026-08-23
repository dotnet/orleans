---
title: Stream with RabbitMQ
description: Configure RabbitMQ Streams as an Orleans persistent stream provider.
ms.date: 08/10/2026
ms.topic: how-to
---

# Stream with RabbitMQ

The [`Microsoft.Orleans.Streaming.RabbitMQ`](https://www.nuget.org/packages/Microsoft.Orleans.Streaming.RabbitMQ) package connects Orleans persistent streams to [RabbitMQ Streams](https://www.rabbitmq.com/docs/streams). Each Orleans queue maps to a RabbitMQ stream. RabbitMQ retains events independently of the Orleans silos, and the provider stores a consumer offset after cached messages have been delivered.

The package is in alpha. Validate delivery, recovery, retention, and throughput behavior under representative failure and load conditions before production use.

## Prerequisites

Enable the RabbitMQ stream plugin and expose the stream protocol port, which defaults to `5552`. The regular AMQP port `5672` isn't used by this provider.

For local development, the RabbitMQ streaming sample in `samples/Streaming/RabbitMQ` uses an [Aspire AppHost](https://aspire.dev/get-started/app-host/) to run RabbitMQ, enable its stream and management plugins, inject generated credentials, and start the Orleans silo:

```shell
dotnet run --project samples/Streaming/RabbitMQ/RabbitMQ.AppHost
```

The AppHost exposes the RabbitMQ stream protocol on port `5552` and configures RabbitMQ to advertise that endpoint. When RabbitMQ runs behind another orchestrator or load balancer, configure its advertised stream host and port so that clients can reach the endpoint returned by RabbitMQ.

## Configure the silo

Install the package and register a named stream provider on every silo:

:::code language="csharp" source="snippets/rabbitmq/RabbitMQConfiguration.cs" id="rabbitmq_silo":::

`PubSubStore` persists explicit Orleans subscription records. Use durable grain storage for `PubSubStore` in production. RabbitMQ offsets and Orleans subscription records serve different purposes and both must survive replacement deployments.

Don't store production credentials in source code. Populate `StreamSystemConfig` from protected configuration or a secret store. RabbitMQ Streams supports multiple endpoints through `StreamSystemConfig`; use stable, reachable advertised addresses for clustered RabbitMQ deployments.

## Configure standalone clients

Register the same provider name, RabbitMQ connection, queue names, and partition count on every standalone Orleans client which accesses the stream provider:

:::code language="csharp" source="snippets/rabbitmq/RabbitMQConfiguration.cs" id="rabbitmq_client":::

The client registration creates an isolated named provider and closes its producer resources with the Orleans client lifecycle.

## Partitioning and queue names

`ConfigurePartitioning` controls the number of Orleans queues and therefore the maximum physical receive parallelism. The provider creates one RabbitMQ stream per queue. Keep the Orleans provider name and partition count stable across restarts so that a replacement deployment uses the same streams and offsets.

Set `RabbitMQClientOptions.QueueNames` only when infrastructure owns the RabbitMQ stream names. Every configured name must identify a distinct stream available to the Orleans deployment.

## Offsets, replay, and retention

The provider advances a RabbitMQ consumer offset after all active Orleans cursors have processed the corresponding cached messages. `IntervalToUpdateOffset` limits how frequently offsets are stored. A shorter interval reduces replay after an unclean shutdown but increases offset writes.

The pulling agent uses `BatchContainerBatchSize = 1` so each delivery result protects one cache entry and checkpoint. Configuration validation enforces this value.

Delivery is at least once across failures. A silo can stop after delivering an event but before persisting the new offset, causing the event to be replayed. Consumers must be idempotent.

Receiver recovery resumes from the last stored RabbitMQ offset while the corresponding entries remain within RabbitMQ retention. New subscriptions begin at the provider's current position. Configure maximum age or length in RabbitMQ according to the required receiver recovery window, and monitor disk capacity, stream growth, consumer lag, connection failures, and Orleans streaming metrics.

New streams default to a maximum length of 200 MiB. Set `RabbitMQClientOptions.StreamOptions.MaxLengthBytes` to select a capacity which matches the recovery window and available broker storage. RabbitMQ applies its configured retention policy as new entries arrive.

## Run the sample

From the repository root:

```shell
dotnet run --project samples/Streaming/RabbitMQ/RabbitMQ.AppHost
```

The Aspire dashboard links to the RabbitMQ management UI and shows the broker and silo health and logs. The sample publishes an event every two seconds and logs the implicitly subscribed consumer. Press <kbd>Ctrl</kbd>+<kbd>C</kbd> to stop the AppHost and its resources.
