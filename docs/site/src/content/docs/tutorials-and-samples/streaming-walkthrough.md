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

Without `Secrets.json`, both hosts register the named in-memory stream provider. The client asks a producer grain to publish integer events. The stream identity determines which implicitly subscribed consumer grain receives each event.

Observe these transitions in the silo log:

1. `ConsumerGrain` activates when the first matching event arrives.
1. Orleans supplies an <xref:Orleans.Streams.IStreamSubscriptionHandleFactory>.
1. The grain resumes the subscription with its observer.
1. `OnNextAsync` logs the event and its sequence token.

No caller creates the consumer activation directly. <xref:Orleans.ImplicitStreamSubscriptionAttribute> maps the stream namespace and identity to the matching grain identity.

## Trace the code

Read these files in order:

1. [`SiloHost/Program.cs`](https://github.com/dotnet/orleans/blob/main/samples/Streaming/Simple/SiloHost/Program.cs) registers `PubSubStore` and the stream provider.
1. [`Grains/ProducerGrain.cs`](https://github.com/dotnet/orleans/blob/main/samples/Streaming/Simple/Grains/ProducerGrain.cs) obtains a typed stream and publishes events.
1. [`Grains/ConsumerGrain.cs`](https://github.com/dotnet/orleans/blob/main/samples/Streaming/Simple/Grains/ConsumerGrain.cs) declares the implicit subscription and attaches an observer.
1. [`Client/Program.cs`](https://github.com/dotnet/orleans/blob/main/samples/Streaming/Simple/Client/Program.cs) drives the scenario.

The `PubSubStore` name is significant. Persistent stream providers use it for subscription metadata; register durable storage under that name in a production cluster.

## Switch to a persistent provider

Create an Azure Event Hubs namespace, event hub, consumer group, and Azure Storage account in a nonproduction subscription. Copy the sample's `Secrets.example.json` shape if present in your checkout and create `Secrets.json` with the Event Hubs and storage connection values. Don't commit this file.

Restart the silo and client. The startup log should now report Azure Event Hub streaming instead of in-memory streaming. The silo configures:

- Azure Table grain storage for `PubSubStore`;
- the Event Hubs stream provider;
- an Azure Table checkpointer; and
- the configured Event Hubs consumer group.

For deployed applications, replace local secrets with managed identity or your platform's secret store and grant only the required data-plane permissions.

## Verify recovery

1. Publish a sequence of identifiable events and record the latest observed item.
1. Stop the silo gracefully, leaving Event Hubs and Azure Storage running.
1. Restart the silo with the same service ID, cluster ID, provider name, hub, and consumer group.
1. Publish more events and verify that the consumer resumes without recreating the subscription.
1. Confirm that processing continues from the stored checkpoint rather than replaying the complete partition.

Orleans stream delivery is at-least-once. A consumer can observe duplicates around failures, so production handlers should be idempotent or deduplicate using an application-owned event identity. Don't treat a sequence token as a globally meaningful business identifier.

## Test failure boundaries

Repeat the recovery check while terminating a silo abruptly. Monitor queue lag, delivery retries, duplicate processing, poison-event handling, and checkpoint age. Then use [streaming operations](../streaming/streaming-operations.md) to define alerts and recovery procedures for the selected provider.

For provider choices and guarantees, continue with [stream providers](../streaming/stream-providers.md), [delivery semantics](../streaming/delivery-semantics.md), and [pub-sub storage](../streaming/pubsub-storage.md).
