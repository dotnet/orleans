---
title: Build and recover an Orleans streaming application
description: Run producers and implicit consumers locally, then configure a persistent stream provider and verify recovery.
ms.date: 08/11/2026
ms.topic: tutorial
---

# Build and recover an Orleans streaming application

This walkthrough uses the maintained [Simple Streaming sample](https://github.com/dotnet/orleans/tree/main/samples/Streaming/Simple) to follow an event from a producer grain to an implicit consumer. You first run with an in-memory provider, then switch to Azure Event Hubs and verify recovery.

## Run the local path

From an empty directory:

```powershell
git clone https://github.com/dotnet/orleans.git
cd orleans\samples\Streaming\Simple
dotnet build .\Streaming.sln
```

Start the silo, then the client in another terminal:

```powershell
dotnet run --project .\SiloHost
dotnet run --project .\Client
```

The default local configuration registers the named in-memory stream provider on both hosts. The client asks a producer grain to publish integer events. The stream identity determines which implicitly subscribed consumer grain receives each event.

Observe these transitions in the silo log:

1. `ConsumerGrain` activates when the first matching event arrives.
1. Orleans supplies an <xref:Orleans.Streams.Core.IStreamSubscriptionHandleFactory>.
1. The grain resumes the subscription with its observer.
1. `OnNextAsync` logs the event and its sequence token.

<xref:Orleans.ImplicitStreamSubscriptionAttribute> maps the stream namespace and identity to the consumer grain identity and activates that consumer when matching events arrive.

## Trace the code

Read these files in order:

1. [`SiloHost/Program.cs`](https://github.com/dotnet/orleans/blob/main/samples/Streaming/Simple/SiloHost/Program.cs) registers `PubSubStore` and the stream provider.
1. [`Grains/ProducerGrain.cs`](https://github.com/dotnet/orleans/blob/main/samples/Streaming/Simple/Grains/ProducerGrain.cs) obtains a typed stream and publishes events.
1. [`Grains/ConsumerGrain.cs`](https://github.com/dotnet/orleans/blob/main/samples/Streaming/Simple/Grains/ConsumerGrain.cs) declares the implicit subscription and attaches an observer.
1. [`Client/Program.cs`](https://github.com/dotnet/orleans/blob/main/samples/Streaming/Simple/Client/Program.cs) drives the scenario.

The `PubSubStore` name is significant. Persistent stream providers use it for subscription metadata; register durable storage under that name in a production cluster.

## Switch to a persistent provider

Create an Azure Event Hubs namespace, an event hub named `my-path`, a consumer group named `my-group`, and an Azure Storage account in a nonproduction subscription. Create `Secrets.json` in the sample directory with this content, replacing both values with connection strings. Keep `Secrets.json` outside source control.

```json
{
  "DataConnectionString": "<Azure Storage connection string>",
  "EventHubConnectionString": "<Event Hubs namespace connection string>"
}
```

Restart the silo and client. The startup log should report the provider switch from in-memory streaming to Azure Event Hubs. The silo configures:

- Azure Table grain storage for `PubSubStore`;
- the Event Hubs stream provider;
- an Azure Table checkpointer; and
- the configured Event Hubs consumer group.

For deployed applications, replace local secrets with managed identity or your platform's secret store and grant only the required data-plane permissions.

## Verify recovery

Before this exercise, replace `Guid.NewGuid()` in `Client/Program.cs` with a fixed GUID so that each client run publishes to the same stream.

1. Run the silo and client, then record the latest sequence token observed by the consumer.
1. Stop the client, then stop the silo gracefully, leaving Event Hubs and Azure Storage running.
1. Restart the silo with the same service ID, cluster ID, provider name, hub, and consumer group.
1. Restart the client so that it calls `StartProducing` for the same stream and recreates the producer's activation-scoped timer.
1. Verify that the consumer resumes its existing subscription at the stored checkpoint and processes events following that checkpoint.

Delivery guarantees are provider-specific. In this Event Hubs scenario, a consumer can observe duplicates around failures, so production handlers should be idempotent or deduplicate using an application-owned event identity. Sequence tokens represent provider delivery position; application-owned event identities support business deduplication.

## Test failure boundaries

Repeat the recovery check while terminating a silo abruptly. Monitor queue lag, delivery retries, duplicate processing, poison-event handling, and checkpoint age. Then use [streaming operations](../streaming/streaming-operations.md) to define alerts and recovery procedures for the selected provider.

For provider choices and guarantees, continue with [stream providers](../streaming/stream-providers.md), [delivery semantics](../streaming/delivery-semantics.md), and [pub-sub storage](../streaming/pubsub-storage.md).
