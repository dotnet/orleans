---
title: Streaming with Orleans
description: Learn how to work with streaming in .NET Orleans.
ms.date: 07/03/2024
zone_pivot_groups: orleans-version
---

# Streaming with Orleans

:::zone target="docs" pivot="orleans-10-0,orleans-9-0,orleans-8-0,orleans-7-0"

Orleans streaming is a feature of the Orleans framework that enables developers to write reactive applications that operate on a sequence of events in a structured way. Orleans streaming provides a set of abstractions and APIs that make thinking about and working with streams simpler and more robust. A stream is a logical entity that always exists and can never fail. Streams are identified by their <xref:Orleans.Runtime.StreamId>. Streams allow the generation of data to be decoupled from its processing, both in time and space. Streams work uniformly across grains and Orleans clients, and can be compatible with and portable across a wide range of existing queuing technologies, such as Event Hubs, ServiceBus, Azure Queues, and Apache Kafka. Orleans streaming also supports dynamic stream bindings, transparent stream consumption lifecycle management, and extensible stream providers.

:::zone-end

:::zone target="docs" pivot="orleans-3-x"

Orleans v.1.0.0 added support for streaming extensions to the programming model. Streaming extensions provide a set of abstractions and APIs that make thinking about and working with streams simpler and more robust. Streaming extensions allow developers to write reactive applications that operate on a sequence of events in a structured way. The extensibility model of stream providers makes the programming model compatible with and portable across a wide range of existing queuing technologies, such as [Event Hubs](https://azure.microsoft.com/services/event-hubs/), [ServiceBus](https://azure.microsoft.com/services/service-bus/), [Azure Queues](/azure/storage/queues/storage-quickstart-queues-dotnet), and [Apache Kafka](https://kafka.apache.org/). There is no need to write special code or run dedicated processes to interact with such queues.

:::zone-end

:::zone target="docs" pivot="orleans-10-0,orleans-9-0,orleans-8-0,orleans-7-0"
:::zone-end

:::zone target="docs" pivot="orleans-3-x"

## Why should I care?

If you already know all about [Stream Processing](https://confluentinc.wordpress.com/2015/01/29/making-sense-of-stream-processing/) and are familiar with technologies like [Event Hubs](https://azure.microsoft.com/services/event-hubs/), [Kafka](https://kafka.apache.org/), [Azure Stream Analytics](https://azure.microsoft.com/services/stream-analytics/), [Apache Storm](https://storm.apache.org/), [Apache Spark Streaming](https://spark.apache.org/streaming/), and [Reactive Extensions (Rx) in .NET](/previous-versions/dotnet/reactive-extensions/hh242985(v=vs.103)), you may be asking why should you care. **Why do we need yet another Stream Processing System and how Actors are related to Streams?** ["Why Orleans Streams?"](streams-why.md) is meant to answer that question.

:::zone-end

## Programming model

There are several principles behind Orleans Streams Programming Model:

1. Orleans streams are *virtual*. That is, a stream always exists. It is not explicitly created or destroyed, and it can never fail.
1. Streams are *identified by* stream IDs, which are just *logical names* comprised of GUIDs and strings.
1. Orleans Streams allow you to *decouple the generation of data from its processing, both in time and space*. That means that the stream producer and the stream consumer may be on different servers or in different time zones, and will withstand failures.
1. Orleans streams are *lightweight and dynamic*. Orleans Streaming Runtime is designed to handle a large number of streams that come and go at a high rate.
1. Orleans stream *bindings are dynamic*. Orleans Streaming Runtime is designed to handle cases where grains connect to and disconnect from streams at a high rate.
1. Orleans Streaming Runtime *transparently manages the lifecycle of stream consumption*. After an application subscribes to a stream, it will then receive the stream's events, even in the presence of failures.
1. Orleans streams *work uniformly across grains and Orleans clients*.

## Quick-start sample

The [Quick Start Sample](streams-quick-start.md) is a good quick overview of the overall workflow of using streams in the application. After reading it, you should read the [Streams Programming APIs](streams-programming-apis.md) to get a deeper understanding of the concepts.

## Stream providers

Streams can come via physical channels of various shapes and forms and can have different semantics. Orleans Streaming is designed to support this diversity via the concept of **Stream Providers**, which is an extensibility point in the system.

:::zone target="docs" pivot="orleans-10-0,orleans-9-0,orleans-8-0,orleans-7-0"

Orleans provides several stream provider implementations:

- [Azure Event Hub stream provider](stream-providers.md#azure-event-hub-stream-provider)
- [Azure Queue (AQ) stream provider](stream-providers.md#azure-queue-aq-stream-provider)
- [Queue adapters](stream-providers.md#queue-adapters)

:::zone-end

:::zone target="docs" pivot="orleans-3-x"

Orleans currently includes several provider implementations:

- **Simple Message** (SMS), which uses direct grain calls and no backing storage system,
- **Azure Queues**, which uses Azure Storage Queues to store messages, and
- **Azure EventHubs**, which uses Azure EventHubs

:::zone-end

For more information, see [Stream Providers](stream-providers.md).

## Stream semantics

**Stream Subscription Semantics**:

Orleans Streams guarantee _Sequential Consistency_ for Stream Subscription operations. Specifically, when a consumer subscribes to a stream, once the <xref:System.Threading.Tasks.Task> representing the subscription operation was successfully resolved, the consumer will see all events that were generated after it has subscribed. In addition, Rewindable streams allow you to subscribe from an arbitrary point in time in the past by using <xref:Orleans.Streams.StreamSequenceToken>. For more information, see [Orleans stream providers](stream-providers.md).

**Individual Stream Events Delivery Guarantees**:

Individual event delivery guarantees depend on individual stream providers. Some provide only best-effort at-most-once delivery (such as Simple Message Streams (SMS) in versions of Orleans before 7.0, thereafter known as [Broadcast Channel](broadcast-channel.md)), while others provide at-least-once delivery (such as Azure Queue Streams). It is even possible to build a streaming provider that will guarantee exactly-once delivery.

**Events Delivery Order**:

Event order also depends on a particular stream provider. In SMS streams, the producer explicitly controls the order of events seen by the consumer by controlling the way it publishes them. Azure Queue streams do not guarantee FIFO order, since the underlying Azure Queues do not guarantee the order in failure cases. Applications can also control their stream delivery ordering by using <xref:Orleans.Streams.StreamSequenceToken>.

## Streams implementation

The [Orleans Streams Implementation](../implementation/streams-implementation/index.md) provides a high-level overview of the internal implementation.

## Code samples

You can find more examples of how to use streaming APIs within a grain at [SampleStreamingGrain.cs](https://github.com/dotnet/orleans/blob/main/test/Grains/TestGrains/SampleStreamingGrain.cs).

## See also

- [Why streams in Orleans?](streams-why.md)
- [Orleans streaming APIs](streams-programming-apis.md)
- [Orleans stream providers](stream-providers.md)
- [Broadcast channels in Orleans](broadcast-channel.md)
- [Orleans Virtual Meetup about Streams](https://www.youtube.com/watch?v=eSepBlfY554)
