---
title: Integrate external stream producers and consumers
description: Connect non-Orleans applications to Orleans streams by defining a provider-specific wire-format adapter.
ms.date: 08/18/2026
ms.topic: how-to
---

# Integrate external stream producers and consumers

An Orleans persistent-stream provider maps its transport messages to logical streams through a provider-specific wire format. An external application interoperates through that format when every participant agrees on:

- How transport messages map to an Orleans <xref:Orleans.Runtime.StreamId>.
- How event types, payloads, and batches are encoded.
- How partitioning, ordering, and sequence positions are represented.
- Which metadata, such as request context, crosses the boundary.

The provider's [data adapter](data-adapters.md) owns this boundary. Azure Event Hubs exposes the full inbound and outbound mapping required for external producers and consumers.

## Use an Event Hubs data adapter

The built-in Azure Event Hubs provider supports a custom <xref:Orleans.Streaming.EventHubs.IEventHubDataAdapter>. Derive from <xref:Orleans.Streaming.EventHubs.EventHubDataAdapter> when its cache representation and checkpoint behavior are suitable, then override the wire-format and stream-mapping behavior defined by the external contract.

Register the adapter with <xref:Orleans.Hosting.EventHubStreamConfiguratorExtensions.UseDataAdapter*>. Configure the same adapter for every silo and Orleans client that uses the provider. A silo needs it to read Event Hubs messages and to publish through Orleans streams; an Orleans client needs it when the client publishes.

The maintained [custom data adapter sample](https://github.com/dotnet/orleans/tree/main/samples/Streaming/CustomDataAdapter) demonstrates an external Event Hubs producer that writes JSON and an Orleans grain that consumes the resulting stream.

## Accept events from an external producer

1. Define the transport contract. Include enough metadata to identify the stream namespace, stream key, event type, and schema version.
1. Map each `EventData` instance to a <xref:Orleans.Runtime.StreamId> in `GetStreamIdentity`. The default Event Hubs adapter uses the Event Hubs partition key as the stream key and the `StreamNamespace` application property as the stream namespace.
1. Decode the payload in the <xref:Orleans.Streams.IBatchContainer> returned by the adapter. `GetEvents<T>` must return only events compatible with the requested Orleans stream type.
1. Register the adapter and subscribe grains or Orleans clients to the resulting stream IDs.
1. Test malformed payloads, unknown schema versions, duplicate delivery, and replay from an older checkpoint before enabling the producer in production.

The adapter determines logical stream identity; Event Hubs determines the physical partition, offset, enqueue time, and retention window. Keep the stream-to-partition mapping stable so that ordering and checkpoint recovery remain predictable.

## Publish events for an external consumer

1. Override `ToQueueMessage<T>` to encode Orleans events in the external consumer's agreed format.
1. Override `GetPartitionKey` when the external contract uses a different partition mapping.
1. Preserve stream identity and schema-version metadata so consumers can route and decode the event without Orleans.
1. Configure the external application as an ordinary Event Hubs consumer, preferably with a consumer group dedicated to that application.

An external consumer participates directly in Event Hubs. Its consumer group controls delivery, checkpoint, retry, and replay behavior, while Orleans pub-sub storage and subscription notifications apply to Orleans stream subscriptions.

If an adapter is intentionally one-way, fail explicitly in the unsupported conversion method. For example, an ingest-only adapter can throw <xref:System.NotSupportedException> from `ToQueueMessage<T>` rather than emitting a message in an unintended format.

## Operational requirements

- Use a dedicated Event Hubs consumer group for the Orleans provider and a different group for each independent external consumer.
- Grant producers, consumers, and checkpoint storage only the permissions they require. Keep credentials and connection strings in protected configuration rather than event properties.
- Version the payload contract and deploy readers before writers when adding a new format.
- Expect duplicate delivery and make consumers idempotent. Event Hubs retention and Orleans checkpoint persistence bound replay and recovery.
- Monitor adapter deserialization failures, Event Hubs lag, checkpoint age, cache pressure, and poison events.
- Load-test the adapter. Payload expansion, deserialization cost, and skewed partition keys can limit throughput before grain execution does.

For delivery and recovery behavior after the event has entered Orleans, see [Stream delivery semantics](delivery-semantics.md) and [Operate Orleans streaming applications](streaming-operations.md).
