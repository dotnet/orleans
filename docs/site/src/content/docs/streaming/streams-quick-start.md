---
title: Orleans streaming quickstart
description: Configure an Orleans in-memory stream, publish events, and consume them with an implicit subscription.
ms.date: 08/02/2026
ms.topic: quickstart
---

# Orleans streaming quickstart

This quickstart uses the `Microsoft.Orleans.Streaming` package and the memory stream provider. It needs no external broker, but both queued data and the `PubSubStore` are in memory. Use this configuration for development and tests, not for production durability.

## Configure the silo

Register the same provider name used by producers and consumers. `PubSubStore` stores explicit stream subscription metadata.

:::code language="csharp" source="snippets/streaming/Configuration.cs" id="memory_silo":::

If an external Orleans client publishes or subscribes, configure the same provider on that client:

:::code language="csharp" source="snippets/streaming/Configuration.cs" id="memory_client":::

## Define the event and stream identity

Stream payloads use the normal Orleans serialization model. The example also centralizes the provider name and stream namespace so producers and consumers construct identical stream identities.

:::code language="csharp" source="snippets/streaming/BasicStreaming.cs" id="stream_contract":::

:::code language="csharp" source="snippets/streaming/BasicStreaming.cs" id="stream_identity":::

The stream namespace is `device-telemetry`, and the stream key is the device ID. The payload type is part of the typed handle, not part of <xref:Orleans.Runtime.StreamId>.

## Publish events

The producer obtains its stream handle during activation and starts an Orleans grain timer when requested:

:::code language="csharp" source="snippets/streaming/BasicStreaming.cs" id="stream_producer":::

Use <xref:Orleans.GrainBaseExtensions.RegisterGrainTimer*> for grain timers. Awaiting `OnNextAsync` waits until the provider accepts responsibility according to its contract; it doesn't generally wait for every consumer to finish. See [Producer acknowledgment](delivery-semantics.md#producer-acknowledgment).

## Consume with an implicit subscription

An implicit subscription maps the stream key to a grain key. Publishing to `device-telemetry/device-17` activates the `DeviceTelemetryGrain` whose string key is `device-17`. Implement <xref:Orleans.Streams.Core.IStreamSubscriptionObserver> to attach the observer supplied by Orleans:

:::code language="csharp" source="snippets/streaming/ImplicitSubscriptions.cs" id="implicit_subscription_grain":::

Obtain `ITemperatureProducerGrain` with the same device key and invoke `StartAsync` to begin publishing. The producer and consumer don't reference one another; they agree on provider name, stream identity, and event type.

Next, read [Streaming APIs](streams-programming-apis.md), then replace the memory provider and memory `PubSubStore` with production services selected from the [provider matrix](stream-providers.md).
