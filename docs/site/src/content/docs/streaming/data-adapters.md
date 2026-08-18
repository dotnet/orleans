---
title: Customize persistent-stream data formats
description: Configure an Orleans persistent-stream data adapter for a versioned queue-message contract.
ms.date: 08/18/2026
ms.topic: how-to
---

# Customize persistent-stream data formats

A persistent-stream data adapter translates between Orleans event batches and a provider's native queue messages. It owns the wire contract: stream identity, event type and payload encoding, schema version, and any request-context values which cross the transport boundary. The provider's queue adapter continues to own transport access, partition assignment, receive acknowledgement, caching, and recovery.

Use a data adapter when the transport remains the same and the application needs a versioned payload format, needs to consume messages produced outside Orleans, or needs to publish messages which an external consumer can decode. Use a [custom queue adapter](custom-queue-adapter.md) when Orleans also needs a new transport or different queue semantics.

## Choose the provider extension point

The built-in providers expose these data-adapter extension points:

| Provider | Data-adapter contract | Registration | Runtime outcome |
|---|---|---|---|
| Azure Queue Storage | <xref:Orleans.Streams.IQueueDataAdapter`2> with `string` queue messages and <xref:Orleans.Streams.IBatchContainer> batches | <xref:Orleans.Hosting.AzureQueueStreamConfiguratorExtensions.ConfigureQueueDataAdapter*> | Replaces encoding and decoding while retaining Azure Queue mapping, visibility, deletion, and non-rewindable delivery |
| Azure Event Hubs | <xref:Orleans.Streaming.EventHubs.IEventHubDataAdapter> | <xref:Orleans.Hosting.EventHubStreamConfiguratorExtensions.UseDataAdapter*> | Replaces wire-format, stream-mapping, and cache conversion behavior while retaining Event Hubs partition reading, checkpointing, and rewindable delivery |

<xref:Orleans.Streams.IQueueDataAdapter`1> defines conversion from an Orleans batch to a native queue message. <xref:Orleans.Streams.IQueueDataAdapter`2> adds conversion from a native message to the batch container delivered by Orleans. Provider-specific contracts can add the position and cache operations required by their transport.

## Define a versioned contract

Define the transport contract independently of CLR assembly names and Orleans serialization internals. A durable envelope normally contains:

- a schema version;
- the stream namespace and key;
- a stable event-type identifier;
- one or more event payloads; and
- an explicit set of cross-process metadata.

Assign each message to exactly one <xref:Orleans.Runtime.StreamId>. Keep the mapping from stream ID to physical partition stable so events for a stream retain the provider's ordering behavior. The adapter receives batches, so the envelope must preserve event order within each batch.

Treat request context as an explicit contract. The example carries a string correlation ID and leaves process-local values inside the producing application.

## Implement an Azure Queue data adapter

The following adapter writes a versioned JSON envelope and reconstructs an <xref:Orleans.Streams.IBatchContainer> when Azure Queue Storage returns the message. The Azure Queue receiver supplies `sequenceId` at read time, and the batch uses it to create a receiver-local sequence token for each event.

:::code language="csharp" source="snippets/streaming/StreamDataAdapters.cs" id="azure_queue_data_adapter":::

The batch container exposes only events compatible with the requested stream type, derives per-event tokens from the queue-message sequence, and imports the metadata defined by the wire contract:

:::code language="csharp" source="snippets/streaming/StreamDataAdapters.cs" id="azure_queue_batch_container":::

Register the same adapter, queue service client, and physical queue names for the provider on every silo and Orleans client which uses it. Silos use the adapter for reads and writes; clients use it when publishing.

:::code language="csharp" source="snippets/streaming/StreamDataAdapters.cs" id="azure_queue_data_adapter_registration":::

Configure durable `PubSubStore` grain storage alongside this registration as described in [Orleans stream providers](stream-providers.md#azure-queue-storage).

## Implement an Event Hubs data adapter

For Event Hubs, derive from <xref:Orleans.Streaming.EventHubs.EventHubDataAdapter> when its cached-message representation and checkpoint behavior fit the application. Override:

- <xref:Orleans.Streaming.EventHubs.EventHubDataAdapter.GetStreamIdentity*> to map each `EventData` instance to a stream;
- <xref:Orleans.Streaming.EventHubs.EventHubDataAdapter.GetBatchContainer*> to decode cached payloads for Orleans consumers;
- <xref:Orleans.Streaming.EventHubs.EventHubDataAdapter.ToQueueMessage*> to encode events published through Orleans; and
- <xref:Orleans.Streaming.EventHubs.EventHubDataAdapter.GetPartitionKey*> to select the physical Event Hubs partition key.

The adapter also participates in cache conversion and sequence positioning through <xref:Orleans.Streaming.EventHubs.IEventHubDataAdapter>. Preserve the Event Hubs offset and sequence number when constructing batch tokens so checkpoint and rewind behavior remains aligned with the partition log.

Register the adapter and Event Hubs connection under the same provider name on silos and publishing clients. The silo registration also configures durable Azure Table checkpoints:

:::code language="csharp" source="snippets/streaming/StreamDataAdapters.cs" id="event_hub_data_adapter_registration":::

The [custom data adapter sample](https://github.com/dotnet/orleans/tree/main/samples/Streaming/CustomDataAdapter) demonstrates a read-side Event Hubs adapter for JSON messages from an external producer. See [Integrate external stream producers and consumers](external-streams.md) for the end-to-end interoperability workflow.

## Evolve the wire contract

Use an expand-and-contract rollout:

1. Deploy readers which accept the current and next schema versions.
1. Change writers to emit the next version.
1. Keep the old reader until the queue or event-log retention window no longer contains the old version.
1. Remove the old version after verifying queue depth, oldest-message age, and checkpoint position.

Keep provider name, stream identity encoding, partition mapping, and sequence interpretation stable during a payload-only migration. A change to any of those values is a stream-topology or recovery migration and needs a separate cutover plan.

Conversion failures surface as stream-delivery failures. Throw a descriptive exception for malformed payloads, unsupported versions, and missing routing metadata so the provider retains or replays the source message according to its delivery semantics. Monitor conversion failures and quarantine poison messages through the transport's operational process before they exhaust retention or block partition progress.

Test both conversion directions with retained messages from every deployed schema version. Include heterogeneous batches, request context, duplicate delivery, malformed envelopes, unknown versions, rolling upgrades, and replay from an older Event Hubs checkpoint.
