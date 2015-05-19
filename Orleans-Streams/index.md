---
layout: page
title: Orleans Streams
---
{% include JB/setup %}

Orleans v.1.0.0 added support for streaming extensions to the programing model. Streaming extensions provide a set of abstractions and APIs that make thinking about and working with streams simpler and more robust. Streaming extensions allow developers to write reactive applications that operate on a sequence of events in a structured way. The extensibility model of stream providers makes the programming model compatible with and portable across a wide range of existing queuing technologies, such as [Event Hubs](http://azure.microsoft.com/en-us/services/event-hubs/), [ServiceBus](http://azure.microsoft.com/en-us/services/service-bus/), [Azure Queues](http://azure.microsoft.com/en-us/documentation/articles/storage-dotnet-how-to-use-queues/), and [Apache Kafka](http://kafka.apache.org/). There is no need to write special code or run dedicated processes to interact with such queues.

## Why should I care?

If you are already familiar about technologies like [Event Hubs](http://azure.microsoft.com/en-us/services/event-hubs/), [Kafka](http://kafka.apache.org/), [Azure Stream Analytics](http://azure.microsoft.com/en-us/services/stream-analytics/), [Stream Processing](http://blog.confluent.io/2015/01/29/making-sense-of-stream-processing/), and [Reactive Extensions (Rx) in .NET](https://msdn.microsoft.com/en-us/data/gg577609.aspx), you may ask why should you care. **Why do we need yet another Stream Processing System and how Actors are related to Streams?** Read [Streaming Use-Cases and Scenarios](Streams-Why) to find out.


## Programming Model

There is a number of principles behind Orleans Streams Programming Model.

1. Following the philosophy of [Orleans virtual actors](https://github.com/dotnet/orleans/wiki/Grains), Orleans streams are *virtual*. That is, a stream always exists. It is not explicitly created or destroyed, and it can never fail.
2. Streams are *identified by* stream ids, which are just *logical names* comprised of GUIDs and strings.
3. Orleans streams are *lightweight and dynamic*. Orleans Streaming Runtime is designed to handle a large number of streams that come and go at a high rate.
4. Orleans stream *bindings are dynamic*. Orleans Streaming Runtime is designed to handle cases where grains connect to and disconnect from streams at a high rate.
5. Orleans Streaming Runtime *transparently manages the lifecycle of streams*. After an application subscribes to a stream, from then on it will receive the stream's events, even in presence of failures.
6. Orleans streams *work uniformly across grains and Orleans clients*.


## Programming APIs

Applications interact with streams via APIs that are very similar to the well known [Reactive Extensions (Rx) in .NET](https://msdn.microsoft.com/en-us/data/gg577609.aspx), by using [`Orleans.Streams.IAsyncStream<T>`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/Core/IAsyncStream.cs) that implements  
[`Orleans.Streams.IAsyncObserver<T>`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/Core/IAsyncObserver.cs) and
[`Orleans.Streams.IAsyncObservable<T>`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/Core/IAsyncObservable.cs) interfaces.

More details can be found in the [Streams Programming APIs](Streams-Programming-APIs).

## Stream Providers

Streams can come in different shapes and forms. Orleans provides a number of different stream implementations, via the concept of **Stream Providers**. In particular, there are unreliable TCP based streams (such as **Simple Message Stream**) and reliable **Persistent Streams** (such as **Azure Queue Stream**). Persistent Streams can work with different queuing technologies via the concept of **Queue Adapters**. In addition, Orleans allows to build **Rewindable Streams**, that allow to subsribe and consume events from the past.

More details on Steam Providers, Queue Adapters and Rewindable Streams can be found at [Stream Providers](Stream-Providers).


## Stream Semantics

**Stream Subsription Semantics**:
We guaratee Sequantial Consistency for Stream Subsription operations. Specificaly, when consumer subscribes to a stream, once the `Task` representing the subsription operation was successfuly resolved, the consumer will see all events that were generated after it has subscribed. In addition, Rewindable streams allow to subscribe from an arbitrary point in time in the past by using `StreamSequenceToken` (more details can be found [here](Stream-Providers)).

**Individual Stream Events Delivery Guarantees**:
Individual event delivery guarantees depend on individual stream providers. Some provide only best-effort at-most-once delivery (such as Simple Message Streams), while others provide at-least-once delivery (such as Azure Queue Streams). It is even possible to build a stream provider that will guarantee exactly-once delivery (we don't have such a provider yet, but it is possible to build one with our [extensability model](Streams-Extensibility)).

## Code Samples

An example of how to use streaming APIs within a grain can be found [here](https://github.com/dotnet/orleans/blob/master/src/TestGrains/SampleStreamingGrain.cs). We plan to create more samples in the future.

## Streams Implementation

The [Orleans Streams Implementation](Streams-Implementation) provides a high level overview of the internal implementation.

## Streams Extensibility

The [Orleans Streams Extensibility](Streams-Extensibility) describes how to extend streams with new functionality.
