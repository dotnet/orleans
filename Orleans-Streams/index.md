---
layout: page
title: Orleans Streams
---
{% include JB/setup %}

Orleans v.1.0.0 added support for streaming extensions to the programing model. Streaming extensions provide a set of abstractions and APIs that make thinking about and working with streams simpler and more robust. Streaming extensions allow developers to write reactive applications that operate on a sequence of events in a structured way. The extensibility model of stream providers makes the programming model compatible with and portable across a wide range of existing queuing technologies, such as [EventHub](http://azure.microsoft.com/en-us/services/event-hubs/), [ServiceBus](http://azure.microsoft.com/en-us/services/service-bus/), [Azure Queues](http://azure.microsoft.com/en-us/documentation/articles/storage-dotnet-how-to-use-queues/), and [Apache Kafka](http://kafka.apache.org/). There is no need to write special code or run dedicated processes to interact with such queues.

## Programming Model

There is a number of principles behind Orleans Streams Programming Model.

1. Following the philosophy of [Orleans virtual actors](https://github.com/dotnet/orleans/wiki/Grains), Orleans streams are *virtual*. That is, a stream always exists. It is not explicitly created or destroyed, and it can never fail.
2. Streams are *identified by* stream ids, which are just *logical names* comprised of GUIDs and strings.
3. Orleans streams are *lightweight and dynamic*. Orleans Streaming Runtime is designed to handle a large number of streams that come and go at a high rate.
4. Orleans stream *bindings are dynamic*. Orleans Streaming Runtime is designed to handle cases where grains connect to and disconnect from streams at a high rate.
5. Orleans Streaming Runtime *transparently manages the lifecycle of streams*. After an application subscribes to a stream, from then on it will receive the stream's events, even in presence of failures.
6. Orleans streams *work uniformly across grains and Orleans clients*.



## Programming APIs

Applications interact with streams via APIs that are very similar to the well known [Reactive Extensions (Rx) in .NET](https://msdn.microsoft.com/en-us/data/gg577609.aspx). The main difference is that Orleans stream extensions are **asynchronous**, to make processing more efficient in Orleans' distributed and scalable compute fabric. 

An application starts by using a *stream provider* to get a handle to a stream. We will see what a stream provider is later, but for now you can think of it as a stream factory that allows implementers to customize streams behavior and semantics:

``` csharp
IStreamProvider streamProvider = base.GetStreamProvider("SimpleStreamProvider"); 
IAsyncStream<int> stream = streamProvider.GetStream<int>(Guid, "MyStreamNamespace"); 
```

[`Orleans.Streams.IAsyncStream<T>`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/Core/IAsyncStream.cs) is a logical, strongly-typed handle to a virtual stream. It is similar in spirit to Orleans Grain Reference. Calls to `GetStreamProvider` and `GetStream` are purely local. The arguments to `GetStream` are a GUID and an additional string, which can be null, that together comprise the stream identity (similar in sprit to the arguments to `GrainFactory.GetGrain`). 

`IAsyncStream<T>` implements both 
[`Orleans.Streams.IAsyncObserver<T>`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/Core/IAsyncObserver.cs) and
[`Orleans.Streams.IAsyncObservable<T>`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/Core/IAsyncObservable.cs) interfaces.
That way an application can use the stream either to produce new events into the stream by using `Orleans.Streams.IAsyncObserver<T>` or to subscribe to and consume events from a stream by using `Orleans.Streams.IAsyncObservable<T>`.

To produce events into the stream, an application just calls `stream.OnNextAsync`.

To subscribe to a stream, an application calls `stream.SubscribeAsync(onNextAsync, onErrorAsync, onCompletedAsync)`. The arguments to `SubscribeAsync` can either be an object that implements the `IAsyncObserver` interface or any combination of the lambda functions to process incoming events. `SubscribeAsync` returns a [`StreamSubscriptionHandle<T>`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/Core/StreamSubscriptionHandle.cs), which is an opaque handle that can be used to unsubscribe from the stream (similar in spirit to an asynchronous version of `IDisposable`).

Orleans streams are **reliable** and their lifecycle is managed transparently by Orleans Streaming Runtime. Specifically, Orleans uses a runtime component called **Streaming Pub Sub** which serves as a rendezvous point for stream consumers and stream producers. Pub Sub tracks all stream subscriptions, persists them, and matches stream consumers with stream producers. In addition to Pub Sub, Orleans Streaming Runtime delivers events from producers to consumers, manages all runtime resources allocated to actively used streams, and transparently garbage collects runtime resources from unused streams.

Orleans streams work **uniformly across grains and Orleans clients**. That is, exactly the same APIs can be used inside a grain and in an Orleans client to produce and consume events. This greatly simplifies the application logic, making special client-side APIs, such as Grain Observers, redundant.


## Stream Providers

Streams can come in different shapes and forms. Some streams may deliver events over direct TCP links, while others deliver events via durable queues. Different stream types may use different batching strategies, different caching algorithms, or different back pressure procedures. We did not want to constrain streaming applications to only a small subset of those behavioral choices. Instead, **Stream Providers** are extensibility points to Orleans Streaming Runtime that allow users to implement any type of stream. This extensibility point is similar in spirit to [Orleans Storage Providers](https://github.com/dotnet/orleans/wiki/Custom%20Storage%20Providers).  Orleans currently ships with two default stream providers: [Simple Message Stream Provider](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/SimpleMessageStream/SimpleMessageStreamProvider.cs) and [Azure Queue Stream Provider](https://github.com/dotnet/orleans/blob/master/src/OrleansProviders/Streams/AzureQueue/AzureQueueStreamProvider.cs).

**Simple Message Stream Provider**, also known as SMS provider, delivers events over TCP by utilizing regular Orleans grain messaging. Since events in SMS are delivered over unreliable TCP links, SMS does _not_ guarantee reliable event delivery and does not automaticaly resend failed messages for SMS streams. The producer of the SMS stream has a way to know if his event was successfully recieved and processed or not: by default the call to `stream.OnNextAsync` returns a `Task` that represents the processing status of the stream consumer. If this Task fails, the producer can decide to send the same event again, thus achieving reliability on ther application level. Although individual stream messages delivery is best effort, SMS streams themselves are reliable. That is, the subscriber-to-producer binding performed by Pub Sub is fully reliable. 


**Azure Queue (AQ) Stream Provider** delivers events over Azure Queues. On the producer side, AQ Stream Provider enqueues events directly into Azure Queue. On the consumer side, AQ Stream Provider manages a set of **pulling agents** that pull events from a set of Azure Queues and deliver them to application code that consumes them. One can think of the pulling agents as a distributed "micro-service" -- a partitioned, highly available, and elastic distributed component. The pulling agents run inside the same silos that host application grains. Thus, there is no need to run separate Azure worker roles to pull from the queues. The existence of pulling agents, their management, backpresure, balancing the queues between them, and handing off queues from a failed agent to another agent are fully managed by Orleans Streaming Runtime and are transparent to application code that uses streams.

## Queue Adapters 

Different stream providers that deliver events over durable queues exhibit similar behavior and are subject to a similar implementation. Therefore, we provide a generic extensible [`PersistentStreamProvider`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/PersistentStreams/PersistentStreamProvider.cs) that allows developers to plug in different types of queues without writing a completely new stream provider from scratch. `PersistentStreamProvider` is parameterized with an [`IQueueAdapter`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/PersistentAdapter/IQueueAdapter.cs), which abstracts specific queue implementation details and provides means to enqueue and dequeue events. All the rest is handled by the logic inside the `PersistentStreamProvider`. Azure Queue Provider mentioned above is also implemented this way: it is an instance of `PersistentStreamProvider` with `AzureQueueAdapter`.

## Rewindable Streams

Some streams only allow an application to subscribe to them starting at the latest point in time, while other streams allow "going back in time". The latter capability is dependent on the underlying queuing technology. For example, Azure Queues only allow consuming the latest enqueued events, while EventHub allows replaying events from an arbitrary point in time (up to some expiration time). Orleans streams expose this capability via a notion of [`StreamSequenceToken`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/Core/StreamSequenceToken.cs). `StreamSequenceToken` is an opaque `IComparable` object that orders events. Streams that support going back in time are called *Rewindable Streams*. 

A producer of a rewindable stream can pass an optional `StreamSequenceToken` to the `OnNext` call. The consumer can pass a `StreamSequenceToken` to the `SubscribeAsync` call and the runtime will deliver events to it starting from that `StreamSequenceToken` (a null token means the consumer wants to receive events starting from the latest.) The ability to rewind a stream is very useful in recovery scenarios. For example, consider a grain that subscribes to a stream and periodically checkpoints its state together with the latest sequence token. When recovering from a failure, the grain can re-subscribe to the same stream from the latest checkpointed sequence token, thereby recovering without losing any events that were generated since the last checkpoint.

## Code Samples

An example of how to use streaming APIs within a grain can be found [here](https://github.com/dotnet/orleans/blob/master/src/TestGrains/SampleStreamingGrain.cs). We plan to create more samples in the future.

## Steam Extensibility

The [Orleans Streams Extensibility](Streams-Extensibility) desribes how to extend streams with new functionality.

***

## API Completeness

The application layer streaming APIs (`IAsyncStream<T>`, `IAsyncObservable<T>`, and `IAsyncObserver<T>`) are quite stable. However, the current Queue Adapter APIs are preliminary. We are currently reconsidering and simplifying them. The end goal is to offer a simple way to implement simple persistent stream adapters, yet allow more complicated providers that use sophisticated caching, back-pressure, and more.
