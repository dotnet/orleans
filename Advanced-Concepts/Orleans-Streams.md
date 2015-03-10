---
layout: page
title: Orleans Streams
---
{% include JB/setup %}

In Orleans v.1.0.0 support for streaming extensions was added to the programing model. Streaming extensions provide a set of abstractions and APIs that make thinking about and working with stream processing much simpler and more robust. Streaming extensions allow developers to write reactive applications that operate on a sequence of events in a structured way. The extensibility model of stream providers makes the programming model compatible with and portable across a wide range of existing queuing technologies, such as [EventHub](http://azure.microsoft.com/en-us/services/event-hubs/), [ServiceBus](http://azure.microsoft.com/en-us/services/service-bus/), [Azure Queues](http://azure.microsoft.com/en-us/documentation/articles/storage-dotnet-how-to-use-queues/), [Apache Kafka](http://kafka.apache.org/), etc., without a need to write a special code or run dedicated processes to interact with such queues.

## Programming Model

Following with the philosophy of [Orleans virtual actors] (https://github.com/dotnet/orleans/wiki/Grains), Orleans streams are also virtual. That is, a stream always exists, it does not have to be explicitly created or destroyed, and it can never fail. Streams are identified by stream ids, which are just logical names for streams comprised from Guids and strings. Orleans Streaming Runtime transparently manages all the lifecycle of streams. An application code can subscribe once to the stream and from now on it will keep receiving the events, even in presence of failures. Orleans Streams work uniformly across grains and Orleans clients.


## Programming APIs

Applications interact with streams via familiar RX extensions, very similar in spirit to [.NET RX extensions](https://msdn.microsoft.com/en-us/data/gg577609.aspx). The main difference is that Orleans stream extensions are asynchronous to make processing more efficient in a distributed and scalable compute fabric of Orleans. 

Application starts by using a stream provider to get a handle to a stream. We will see what a stream provider is later, but for now you can think about it as a stream factory that allows implementers to customize streams' behaviors and semantics:

     IStreamProvider streamProvider = base.GetStreamProvider("SimpleStreamProvider"); 
     IAsyncStream<int> stream = streamProvider.GetStream<int>(Guid, "MyStreamNamespace"); 

[`Orleans.Streams.IAsyncStream<T>`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/Core/IAsyncStream.cs) is a logical, strongly typed handle to the virtual stream. It is similar in spirit to Grain Reference. Both calls to `GetStreamProvider` and `GetStream` are purely local calls. The arguments to `GetStream` are Guid and an additional optional string, which can be null, that together comprise the stream identity (similar in sprit to the arguments to Factory.GetGrain call). 

`IAsyncStream<T>` implements both [`Orleans.Streams.IAsyncObservable<T>`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/Core/IAsyncObservable.cs) and [`Orleans.Streams.IAsyncObserver<T>`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/Core/IAsyncObserver.cs)
That way applications can now use the stream to either produce new events into the stream by using `Orleans.Streams.IAsyncObserver<T>` or subscribe to a stream by using `Orleans.Streams.IAsyncObservable<T>`in order to consume events.

In order to produce events into the stream, an application can just call `stream.OnNextAsync`.

In order to subscribe to the stream, an application can call `stream.SubscribeAsync(onNextAsync, onErrorAsync, onCompletedAsync)`. The arguments to `SubscribeAsync` can either be an object that implements the `IAsyncObserver` interface or any combination of the lambda functions to process incoming events. `SubscribeAsync` returns a [`StreamSubscriptionHandle<T>`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/Core/StreamSubscriptionHandle.cs) which is an opaque handle that can be used to unsubscribe from the stream (similar in spirit to an asynchronous version of IDisposable).


Orleans streams are **reliable** and all their lifecycle is managed transparently by the Orleans Streaming Runtime. Specifically, Orleans uses a runtime component called **Streaming Pub Sub** which serves as a rendezvous for stream consumers and stream producers. The Pub Sub tracks all stream subscriptions, persist them and matches stream consumers with stream producers. In addition to Pub Sub, Orleans streaming runtime delivers events from producers to consumers, manages all runtime resources allocated for actively used streams, and transparently garbage collects runtime resources from unused streams.

Orleans streams work **uniformly across grains and Orleans clients**. That is, exactly the same APIs can be used inside a grain and in Orleans client to both produce and consume events. This greatly simplifies the application logic, making special client-side APIs, such as Grain Observers, redundant.


## Stream Providers

Streams can come in different shapes and forms. Some streams may deliver events over direct TCP links, while others over durable queues. Different stream types may use different batching strategies, different caching algorithms, or different back pressure procedures. We did not want to constrain streaming applications to only a small subset of those behavioral choices. Instead, **Stream Providers** are extensibility points to the Orleans Streaming Runtime and allow implementing any type of streams. This extensibility point is similar in spirit to [Orleans Storage Providers] (https://github.com/dotnet/orleans/wiki/Custom%20Storage%20Providers). Anyone can implement their own Stream Provider. At this point Orleans ships with 2 default stream providers: [Simple Message Stream Provider] (https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/SimpleMessageStream/SimpleMessageStreamProvider.cs) and [Azure Queue Stream Provider](https://github.com/dotnet/orleans/blob/master/src/OrleansProviders/Streams/AzureQueue/AzureQueueStreamProvider.cs).

**Simple Message Stream Provider**, also known as SMS provider is, as its name suggests, a simple stream provider that delivers events over TCP, by utilizing the regular Orleans grain messaging. Since events in SMS are delivered over unreliable TCP links, SMS does NOT guarantee reliable event delivery. Events can get lost.  Please notice that SMS streams themselves are still fully reliable. That is, the subscriber to producer matching, performed by the Pub Sub, is still fully reliable. Only individual message delivery is best effort.

**Azure Queue Stream Provider** delivers events over Azure Queues. On the producer side AQ stream provider enqueues events directly into Azure Queue. On the consumers side AQ stream provider manages a set of **pulling agents** that pull events from a set of Azure Queues and deliver them to the application code that consumes them. One can think about the pulling agents as a distributed "micro-service" - partitioned, highly available, and elastic distributed component. The pulling agents run inside the same silos that host application grains. There is no need to run a separate set of Azure worker roles just to pull from the queues. The existence of the pulling agents, their management, balancing the queues between them, handoff of queues from a failed agent to another agent, all those aspects are fully managed by the Orleans streaming runtime and are transparent to the application code that uses streams.

## Queue Adapters 

Stream providers that deliver events over durable queues exhibit similar behavior and are subject to a similar implementation. Therefore, we provide a generic extensible [`PersistentStreamProvider`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/PersistentStreams/PersistentStreamProvider.cs) that allows developers to plug-in different queues without the need to write a completely new stream provider from scratch. `PersistentStreamProvider` is parameterized with an [`IQueueAdapter`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/PersistentAdapter/IQueueAdapter.cs) which abstracts the specific queue implementation details and provides the means to enqueue and dequeue events. All the rest is handled by the logic inside the `PersistentStreamProvider`. The Azure Queue Provider mentioned above is also implemented this way: it is an instance of `PersistentStreamProvider` with `AzureQueueAdapter`.

## Rewindable Streams

Some streams only allow to subscribe to them from the latest point in time, while other streams allow "going back in time". That capability is dependent on the underlying queuing technology. For example, Azure Queues only allow to consume the latest enqueued events, while EventHub allows to go back and replay the events, from an arbitrary point in time (up to some expiration time). Orleans streams expose this capability via a notion of[`StreamSequenceToken`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/Core/StreamSequenceToken.cs). `StreamSequenceToken` is an opaque `IComparable` object, that allows to order events. Streams that support going back in time are called "Rewindable Streams". 

A producer of a rewindable stream can pass an optional `StreamSequenceToken` to the OnNext call. The consumer can pass a `StreamSequenceToken` to the SubsribeAsync call and the runtime will deliver him events starting from that `StreamSequenceToken`
(null token means the consumer is interested to start receiving events from latest). The ability to rewind a stream is very useful, e.g., in recovery scenarios. Imagine a scenario when a grain subscribes to a stream and periodically checkpoint its state, together with the latest sequence token. In case of a failure, the grain can re-subscribe to the same stream from the latest checkpointed sequence token, thus being able to recover from a failure without loosing any events that were generated since the last checkpoint.


## Code Samples

An example of how to use streaming APIs within a grain can be found [here](https://github.com/dotnet/orleans/blob/master/src/TestGrains/SampleStreamingGrain.cs). We plan to create more samples in the future.

***

## API Completeness

As opposite to the application layer streaming APIs themselves (`IAsyncStream<T>`, `IAsyncObservable<T>`, and `IAsyncObserver<T>`) that are pretty stable, the current Queue Adapters APIs are preliminary. We are currently in the process of revisiting and simplifying them. The end goal is to provide a simple way to implement simple persistent stream adapters while also allowing more complicated providers, that use sophisticated caching, back-pressure, and more.