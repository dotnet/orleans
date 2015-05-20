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
IAsyncStream<T> stream = streamProvider.GetStream<T>(Guid, "MyStreamNamespace"); 
```

[`Orleans.Streams.IAsyncStream<T>`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/Core/IAsyncStream.cs) is a logical, strongly-typed handle to a virtual stream. It is similar in spirit to Orleans Grain Reference. Calls to `GetStreamProvider` and `GetStream` are purely local. The arguments to `GetStream` are a GUID and an additional string that we call a stream namespace (which can be null). Together the GUID and the namespace string comprise the stream identity (similar in sprit to the arguments to `GrainFactory.GetGrain`). The combination of GUID and namespace string provide extra flexibility in determining stream identities. Just like grain 7 may exist within the Grain type `PlayerGrain` and a different grain 7 may exist within the grain type `ChatRoomGrain`, Stream 123 may exist with the stream namespace `PlayerEventsStream` a different stream 123 may exist within the stream namespace type `ChatRoomMessagesStream`.


### Producing and Consuming

`IAsyncStream<T>` implements both 
[`Orleans.Streams.IAsyncObserver<T>`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/Core/IAsyncObserver.cs) and
[`Orleans.Streams.IAsyncObservable<T>`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/Core/IAsyncObservable.cs) interfaces.
That way an application can use the stream either to produce new events into the stream by using `Orleans.Streams.IAsyncObserver<T>` or to subscribe to and consume events from a stream by using `Orleans.Streams.IAsyncObservable<T>`.

To produce events into the stream, an application just calls 

``` csharp
stream.OnNextAsync(int)
```

To subscribe to a stream, an application calls  

``` csharp
StreamSubscriptionHandle<T> subscriptionHandle = await stream.SubscribeAsync(onNextAsync, onErrorAsync, onCompletedAsync)
```

The arguments to `SubscribeAsync` can either be an object that implements the `IAsyncObserver` interface or any combination of the lambda functions to process incoming events. `SubscribeAsync` returns a [`StreamSubscriptionHandle<T>`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/Core/StreamSubscriptionHandle.cs), which is an opaque handle that can be used to unsubscribe from the stream (similar in spirit to an asynchronous version of `IDisposable`)

``` csharp
await subscriptionHandle.UnsubscribeAsync()
```

It is important to note that **the subsription is for a garin, not for an activation**. Once the grain code subsribed to the stream, this subsription surpasses the life of this activation and stays durable forever, untill the grain code (potentialy in a different activation) explicitely unsubsribes. This is the heart of a virtual stream absraction: not only all the streams always exits, logicaly, but also that a stream subsription is durable and lives beyond a particular physical activation that issused this subsription. 


### Multiplicity

An Orleans stream may have multiple producers and multiple consumers. A message published by a producer will be delivered to all consumers that were subscribed to the stream before the message was published.

In addition, the consumer can subscribe to the same stream multiple times. Each time it subscribes it gets back a unique `StreamSubscriptionHandle<T>`. If a grain (or client) is subscribed X times to the same stream, it will receive the same event X times, once for each subscription. The consumer can also unsubscribe from an individual subscription or find out all its current subscriptions, by calling:

``` csharp
await IAsyncStream<T>.GetAllSubscriptionHandles()
```


### Explicit and Implicit Subsriptions

By default, stream consumer has to explicitelly subsribe to the stream. This subsription would usualy be triggered by some external message that the grain (or client) receive that instructs them to subsribe. For example, in a chat service when user joins a chat room his grain receives a `JoinChatGroup` message with the chat name and it will cause the user grain to subscribe to this chat stream (stream of messages published to this chat).

In addition, Orleans Streams also support "Implicit Subsriptions". In this model the grain does not explicitely subscribe to the stream. This grain is subsribed automaticaly, implicitely, just based on its grain identity and `ImplicitStreamSubscription`.

Grain implementation class of type `MyGrainType` can declare an attribute `[ImplicitStreamSubscription("MyStreamNamespace")]`. This tells the streaming runtime that when an event is generated on a stream with GUID XXX and namespace `"MyStreamNamespace"` namespace, it should be delivered to grain XXX of type `MyGrainType`. That is, the consumer grain identity is determined based on stream identity GUID and consumer grain type is determined based on the presense of `ImplicitStreamSubscription` attribute.

The presense of `ImplicitStreamSubscription`causes the streaming runtime to automaticaly subsribe this grain to a stream and deliver the stream events to it. However, the grain code still needs to tell the runtime how it wants events to be processed. Essentialy, it need to attach the `IAsyncObserver`. Therefore, when the grain is activated, the grain code inside `OnActivateAsync` needs to call: 

``` csharp
IStreamProvider streamProvider = base.GetStreamProvider("SimpleStreamProvider"); 
IAsyncStream<T> stream = streamProvider.GetStream<T>(this.GetPrimaryKey(), "MyStreamNamespace"); 
StreamSubscriptionHandle<T> subscription = await stream.SubscribeAsync(IAsyncObserver<T>);  
```

### Stream Order and Sequence Tokens

The order in which events between an individual producer and an individual consumer depends on a particular stream provider.

In SMS stream the producer explicitelly controls the order of events seen by the consumer by controlling the way he publishes them. By default (if the `FireAndForget` options for SMS provider is set to false) and if the producer awaits every `OnNext` call, the events arrive in FIFO order. In SMS it is up to the producer to decide how to handle deliverty fai.ures, taht will be indicated by a broken `Task` returned by the `OnNext` call.

Azure Queue streams do not guarantee FIFO order, since the underlaying Azure Queues do not guarantee order in failure cases (they do guarantee FIFO order in faliure free executions). When a producer produces the event into Azure Queue, if the enqueue operatin failed, it is up to the producer to attempt another enqueue and later on deal with potential duplicates messages. On the delivery side, Orleans Streaming runtime dequeue the event from the Azure Queue and attmerpts to deliver it for procesing to consmyus. Orleans Streaming runtime deletes the ecvent from the queue only upon successfull processing. If the delivety or procesing failed, the event is not dekete from the queu and will automatically re-appear in the queue much later. The Streaming runtime will try to deliver it again, thus potentially breaking the FIFO order. The described behaivour matches the regular semantics of Azure Queues. 

To deal with the above ordering issues, application can specify its own ordering. This is achived via a notion of [`StreamSequenceToken`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/Core/StreamSequenceToken.cs). `StreamSequenceToken` is an opaque `IComparable` object that can be used to order events. 
A producer can pass an optional `StreamSequenceToken` to the `OnNext` call. This `StreamSequenceToken` will be passed all the way to the consumer and will be delivered together with the event. That way, application can reason and reconstruct it's order independantly from the streaming runtime.

The consumer can pass a `StreamSequenceToken` to the `SubscribeAsync` call and the runtime will deliver events to it starting from that `StreamSequenceToken` (a null token means the consumer wants to receive events starting from the latest).


### Rewindable Streams

Some streams only allow application to subscribe to them starting at the latest point in time, while other streams allow "going back in time". The latter capability is dependent on the underlying queuing technology and the particulal stream provider. For example, Azure Queues only allow consuming the latest enqueued events, while EventHub allows replaying events from an arbitrary point in time (up to some expiration time).Streams that support going back in time are called *Rewindable Streams*. 

The consumer of a rewindable stream can pass a `StreamSequenceToken` to the `SubscribeAsync` call and the runtime will deliver events to it starting from that `StreamSequenceToken` (a null token means the consumer wants to receive events starting from the latest).

The ability to rewind a stream is very useful in recovery scenarios. For example, consider a grain that subscribes to a stream and periodically checkpoints its state together with the latest sequence token. When recovering from a failure, the grain can re-subscribe to the same stream from the latest checkpointed sequence token, thereby recovering without losing any events that were generated since the last checkpoint.

**Current Status of Rewindable Streams**
Both SMS and Azure Queue providers are not-rewinable and Orleans currentkly does not include an implementation of rewindable stream. We are actively wokring on this.



### Stateless Automatically Scaled-Out Processing

By default Orleans streams are targeted to support a large number of relatively small streams, each is processed by one or more statefull grains. Collectively, the processing of all the stream together is sharded among a large number of regular (statefull) grains. The application code controls this sharding by assigning stream ids, grain ids and explicitely subscribing. **The goal is sharded statefull processing**. 

However, there is also an interesting scenario of automaticaly scaled-out stateless processing. In this scenario application has a small number (or even one) large stream and the goal is a stateless processing. For example, a global stream of all messages for all my events and the processing will involve soem kind of decoding/deciphering and potentially forwarding them for further statefull processing into another set of streams. The stateless scaled-out stream processing can be suppported in Orleans via `StatelessWorker` grains.

This is currently not implemented (due to priority constrains). An attempt to subsribe to a stream from a `StatelessWorker` grains will result in undefined behaivour. We are currently considering to support this option.

### Grains and Orleans clients

Orleans streams work **uniformly across grains and Orleans clients**. That is, exactly the same APIs can be used inside a grain and in an Orleans client to produce and consume events. This greatly simplifies the application logic, making special client-side APIs, such as Grain Observers, redundant.


### Fully Managed and Reliable

Orleans streams are **reliable** and their lifecycle is managed transparently by Orleans Streaming Runtime. Specifically, Orleans uses a runtime component called **Streaming Pub Sub** which serves as a rendezvous point for stream consumers and stream producers. Pub Sub tracks all stream subscriptions, persists them, and matches stream consumers with stream producers. In addition to Pub Sub, Orleans Streaming Runtime delivers events from producers to consumers, manages all runtime resources allocated to actively used streams, and transparently garbage collects runtime resources from unused streams.
