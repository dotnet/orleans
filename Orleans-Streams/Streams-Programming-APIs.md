---
layout: page
title: Orleans Streams Programming APIs
---
{% include JB/setup %}

Applications interact with streams via APIs that are very similar to the well known [Reactive Extensions (Rx) in .NET](https://msdn.microsoft.com/en-us/data/gg577609.aspx). The main difference is that Orleans stream extensions are **asynchronous**, to make processing more efficient in Orleans' distributed and scalable compute fabric. 

### Stream Providers and Async Streams

An application starts by using a *stream provider* to get a handle to a stream. We will see what a stream provider is later, but for now you can think of it as a stream factory that allows implementers to customize streams behavior and semantics:

``` csharp
IStreamProvider streamProvider = base.GetStreamProvider("SimpleStreamProvider"); 
IAsyncStream<int> stream = streamProvider.GetStream<int>(Guid, "MyStreamNamespace"); 
```

[`Orleans.Streams.IAsyncStream<T>`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/Core/IAsyncStream.cs) is a logical, strongly-typed handle to a virtual stream. It is similar in spirit to Orleans Grain Reference. Calls to `GetStreamProvider` and `GetStream` are purely local. The arguments to `GetStream` are a GUID and an additional string that we call a stream namespace (which can be null). Together the GUID and the namespace string comprise the stream identity (similar in sprit to the arguments to `GrainFactory.GetGrain`). The combination of GUID and namespace string provide extra flexibility in determining stream identities. Just like grain 7 may exist within the Grain type `PlayerGrain` and a different grain 7 may exist within the grain type `ChatRoomGrain`, Stream 123 may exist with the stream namespace `PlayerEventsStream` a different stream 123 may exist within the stream namespace type `ChatRoomMessagesStream`.


### Producing and Consuming

`IAsyncStream<T>` implements both 
[`Orleans.Streams.IAsyncObserver<T>`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/Core/IAsyncObserver.cs) and
[`Orleans.Streams.IAsyncObservable<T>`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/Core/IAsyncObservable.cs) interfaces.
That way an application can use the stream either to produce new events into the stream by using `Orleans.Streams.IAsyncObserver<T>` or to subscribe to and consume events from a stream by using `Orleans.Streams.IAsyncObservable<T>`.

To produce events into the stream, an application just calls `stream.OnNextAsync`.

To subscribe to a stream, an application calls `stream.SubscribeAsync(onNextAsync, onErrorAsync, onCompletedAsync)`. The arguments to `SubscribeAsync` can either be an object that implements the `IAsyncObserver` interface or any combination of the lambda functions to process incoming events. `SubscribeAsync` returns a [`StreamSubscriptionHandle<T>`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/Core/StreamSubscriptionHandle.cs), which is an opaque handle that can be used to unsubscribe from the stream (similar in spirit to an asynchronous version of `IDisposable`).

### Multiplicity

An Orleans stream may have multiple producers and multiple consumers. A message published by a producer will be delivered to all consumers that were subscribed to the stream before the message was published.


### Explicit and Implicit Subsriptions

By default, stream consumer has to explicitelly subsribe to the stream. This subsription would usualy be triggered by some external message that the grain (or client) receive that instructs them to subsribe. For example, in a chat service when user joins a chat room his grain receives a `JoinChatGroup` message with the chat name and it will cause the user grain to subscribe to this chat stream (stream of messages published to this chat).

In addition, Orleans Streams also support "Implicit Subsriptions". In this model the grain does not to explicitely subscribe to the stream. This grain is subsribed automaticaly, impictely, by the streaming runtime, just based on its grain identity.

Grain implementation class can have an attribute `[ImplicitStreamSubscription("MyStreamNamespace")]`. This


### Grains and Orleans clients

Orleans streams work **uniformly across grains and Orleans clients**. That is, exactly the same APIs can be used inside a grain and in an Orleans client to produce and consume events. This greatly simplifies the application logic, making special client-side APIs, such as Grain Observers, redundant.

### Rewindable Streams

Some streams only allow an application to subscribe to them starting at the latest point in time, while other streams allow "going back in time". The latter capability is dependent on the underlying queuing technology. For example, Azure Queues only allow consuming the latest enqueued events, while EventHub allows replaying events from an arbitrary point in time (up to some expiration time). Orleans streams expose this capability via a notion of [`StreamSequenceToken`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/Core/StreamSequenceToken.cs). `StreamSequenceToken` is an opaque `IComparable` object that orders events. Streams that support going back in time are called *Rewindable Streams*. 

A producer of a rewindable stream can pass an optional `StreamSequenceToken` to the `OnNext` call. The consumer can pass a `StreamSequenceToken` to the `SubscribeAsync` call and the runtime will deliver events to it starting from that `StreamSequenceToken` (a null token means the consumer wants to receive events starting from the latest.) The ability to rewind a stream is very useful in recovery scenarios. For example, consider a grain that subscribes to a stream and periodically checkpoints its state together with the latest sequence token. When recovering from a failure, the grain can re-subscribe to the same stream from the latest checkpointed sequence token, thereby recovering without losing any events that were generated since the last checkpoint.


### Fully Managed and Reliable

Orleans streams are **reliable** and their lifecycle is managed transparently by Orleans Streaming Runtime. Specifically, Orleans uses a runtime component called **Streaming Pub Sub** which serves as a rendezvous point for stream consumers and stream producers. Pub Sub tracks all stream subscriptions, persists them, and matches stream consumers with stream producers. In addition to Pub Sub, Orleans Streaming Runtime delivers events from producers to consumers, manages all runtime resources allocated to actively used streams, and transparently garbage collects runtime resources from unused streams.
