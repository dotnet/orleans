---
layout: page
title: Orleans Streams Programming APIs
---

# Orleans Streams Programming APIs

Applications interact with streams via APIs that are very similar to the well known [Reactive Extensions (Rx) in .NET](https://msdn.microsoft.com/en-us/data/gg577609.aspx). The main difference is that Orleans stream extensions are **asynchronous**, to make processing more efficient in Orleans' distributed and scalable compute fabric.

### Async Stream<a name="Async-Stream"></a>

An application starts by using a *stream provider* to get a handle to a stream. You can read more about stream providers [here](stream_providers.md), but for now you can think of it as stream factory that allows implementers to customize streams behavior and semantics:

``` csharp
IStreamProvider streamProvider = base.GetStreamProvider("SimpleStreamProvider");
IAsyncStream<T> stream = streamProvider.GetStream<T>(Guid, "MyStreamNamespace");
```

An application can get a reference to the stream provider either by calling the `GetStreamProvider` method on the `Grain` class when inside a grain, or by calling the `GrainClient.GetStreamProvider()` method when on the client.

[**`Orleans.Streams.IAsyncStream<T>`**](https://github.com/dotnet/orleans/blob/master/src/Orleans.Core.Abstractions/Streams/Core/IAsyncStream.cs) is a **logical, strongly-typed handle to a virtual stream**. It is similar in spirit to Orleans Grain Reference. Calls to `GetStreamProvider` and `GetStream` are purely local. The arguments to `GetStream` are a GUID and an additional string that we call a stream namespace (which can be null). Together the GUID and the namespace string comprise the stream identity (similar in sprit to the arguments to `GrainFactory.GetGrain`). The combination of GUID and namespace string provide extra flexibility in determining stream identities. Just like grain 7 may exist within the Grain type `PlayerGrain` and a different grain 7 may exist within the grain type `ChatRoomGrain`, Stream 123 may exist with the stream namespace `PlayerEventsStream` and a different stream 123 may exist within the stream namespace `ChatRoomMessagesStream`.


### Producing and Consuming<a name="Producing-and-Consuming"></a>

`IAsyncStream<T>` implements both
[**`Orleans.Streams.IAsyncObserver<T>`**](https://github.com/dotnet/orleans/blob/master/src/Orleans.Core.Abstractions/Streams/Core/IAsyncObserver.cs) and
[**`Orleans.Streams.IAsyncObservable<T>`**](https://github.com/dotnet/orleans/blob/master/src/Orleans.Core.Abstractions/Streams/Core/IAsyncObservable.cs) interfaces.
That way an application can use the stream either to produce new events into the stream by using `Orleans.Streams.IAsyncObserver<T>` or to subscribe to and consume events from a stream by using `Orleans.Streams.IAsyncObservable<T>`.

``` csharp
public interface IAsyncObserver<in T>
{
    Task OnNextAsync(T item, StreamSequenceToken token = null);
    Task OnCompletedAsync();
    Task OnErrorAsync(Exception ex);
}

public interface IAsyncObservable<T>
{
    Task<StreamSubscriptionHandle<T>> SubscribeAsync(IAsyncObserver<T> observer);
}
```

To produce events into the stream, an application just calls

``` csharp
await stream.OnNextAsync<T>(event)
```

To subscribe to a stream, an application calls

``` csharp
StreamSubscriptionHandle<T> subscriptionHandle = await stream.SubscribeAsync(IAsyncObserver)
```

The argument to `SubscribeAsync` can either be an object that implements the `IAsyncObserver` interface or a combination of
lambda functions to process incoming events. More options for `SubscribeAsync` are available via [**`AsyncObservableExtensions`**](https://github.com/dotnet/orleans/blob/master/src/Orleans.Core.Abstractions/Streams/Extensions/AsyncObservableExtensions.cs) class.
`SubscribeAsync` returns a [**`StreamSubscriptionHandle<T>`**](https://github.com/dotnet/orleans/blob/master/src/Orleans.Core.Abstractions/Streams/Core/StreamSubscriptionHandle.cs), which is an opaque handle that can be used to unsubscribe from the stream (similar in spirit to an asynchronous version of `IDisposable`).

``` csharp
await subscriptionHandle.UnsubscribeAsync()
```

It is important to note that **the subscription is for a grain, not for an activation**. Once the grain code is subscribed to the stream, this subscription surpasses the life of this activation and stays durable forever, until the grain code (potentially in a different activation) explicitly unsubscribes. This is the heart of a **virtual stream abstraction**: not only do all streams always exist, logically, but also a stream subscription is durable and lives beyond a particular physical activation that created the subscription.

### Multiplicity<a name="Multiplicity"></a>

An Orleans stream may have multiple producers and multiple consumers. A message published by a producer will be delivered to all consumers that were subscribed to the stream before the message was published.

In addition, the consumer can subscribe to the same stream multiple times. Each time it subscribes it gets back a unique `StreamSubscriptionHandle<T>`. If a grain (or client) is subscribed X times to the same stream, it will receive the same event X times, once for each subscription. The consumer can also unsubscribe from an individual subscription. It can find all its current subscriptions by calling:

``` csharp
IList<StreamSubscriptionHandle<T>> allMyHandles = await IAsyncStream<T>.GetAllSubscriptionHandles()
```

### Recovering From Failures<a name="Recovering-From-Failures"></a>

If the producer of a stream dies (or its grain is deactivated), there is nothing it needs to do. The next time this grain wants to produce more events it can get the stream handle again and produce new events in the same way.

Consumer logic is a little bit more involved. As we said before, once a consumer grain is subscribed to a stream, this subscription is valid until the grain explicitly unsubscribes. If the consumer of the stream dies (or its grain is deactivated) and a new event is generated on the stream, the consumer grain will be automatically re-activated (just like any regular Orleans grain is automatically activated when a message is sent to it). The only thing that the grain code needs to do now is to provide an `IAsyncObserver<T>` to process the data. The consumer basically needs to re-attach processing logic as part of the `OnActivateAsync` method. To do that it can call:

``` csharp
StreamSubscriptionHandle<int> newHandle = await subscriptionHandle.ResumeAsync(IAsyncObserver)
```

The consumer uses the previous handle it got when it first subscribed in order to "resume processing". Notice that `ResumeAsync` merely updates an existing subscription with the new instance of `IAsyncObserver` logic and does not change the fact that this consumer is already subscribed to this stream.

How does the consumer get an old subscriptionHandle? There are 2 options. The consumer may have persisted the handle it was given back from the original `SubscribeAsync` operation and can use it now. Alternatively, if the consumer does not have the handle, it can ask the `IAsyncStream<T>` for all its active subscription handles, by calling:

``` csharp
IList<StreamSubscriptionHandle<T>> allMyHandles = await IAsyncStream<T>.GetAllSubscriptionHandles()
```
The consumer can now resume all of them, or unsubscribe from some if it wishes to.

**COMMENT:** If the consumer grain implements the `IAsyncObserver` interface directly (`public class MyGrain<T> : Grain, IAsyncObserver<T>`), it should in theory not be required to re-attach the `IAsyncObserver` and thus will not need to call `ResumeAsync`. The streaming runtime should be able to automatically figure out that the grain already implements `IAsyncObserver` and will just invoke those `IAsyncObserver` methods. However, the streaming runtime currently does not support this and the grain code still needs to explicitly call `ResumeAsync`, even if the grain implements `IAsyncObserver` directly. Supporting this is on our TODO list.


### Explicit and Implicit Subscriptions<a name="Explicit-and-Implicit-Subscriptions"></a>

By default, a stream consumer has to explicitly subscribe to the stream. This subscription would usually be triggered by some external message that the grain (or client) receives that instructs it to subscribe. For example, in a chat service when a user joins a chat room his grain receives a `JoinChatGroup` message with the chat name, which will cause the user grain to subscribe to this chat stream.

In addition, Orleans Streams also support **"Implicit Subscriptions"**. In this model the grain does not explicitly subscribe to the stream. This grain is subscribed automatically, implicitly, just based on its grain identity and an `ImplicitStreamSubscription` attribute. Implicit subscriptions' main value is allowing the stream activity to trigger the grain activation (hence triggering the subscription) automatically. For example, using SMS streams, if one grain wanted to produce a stream and another grain process this stream, the producer  would need to know the identity of the consumer grain and make a grain call to it telling it to subscribe to the stream. Only after that can it start sending events. Instead, using implicit subscriptions, the producer can just start producing events to a stream, and the consumer grain will automatically be activated and subscribe to the stream. In that case, the producer doesn't care at all who is reading the events

Grain implementation class of type `MyGrainType` can declare an attribute `[ImplicitStreamSubscription("MyStreamNamespace")]`. This tells the streaming runtime that when an event is generated on a stream whose identity is GUID XXX and `"MyStreamNamespace"` namespace, it should be delivered to the grain whose identity is XXX of type `MyGrainType`. That is, the runtime maps stream `<XXX, MyStreamNamespace>` to consumer grain `<XXX, MyGrainType>`.

The presence  of `ImplicitStreamSubscription`causes the streaming runtime to automatically subscribe this grain to a stream and deliver the stream events to it. However, the grain code still needs to tell the runtime how it wants events to be processed. Essentially, it needs to attach the `IAsyncObserver`. Therefore, when the grain is activated, the grain code inside `OnActivateAsync` needs to call:

``` csharp
IStreamProvider streamProvider = base.GetStreamProvider("SimpleStreamProvider");
IAsyncStream<T> stream = streamProvider.GetStream<T>(this.GetPrimaryKey(), "MyStreamNamespace");
StreamSubscriptionHandle<T> subscription = await stream.SubscribeAsync(IAsyncObserver<T>);
```

### Writing Subscription Logic<a name="Writing-Subscription-Logic"></a>

Below are the guidelines on how to write the subscription logic for various cases: explicit and implicit subscriptions, rewindable and non-rewindable streams. The main difference between explicit and implicit subscriptions is that for implicit the grain always has exactly one implicit subscription for every stream namespace; there is no way to create multiple subscriptions (there is no subscription multiplicity), there is no way to unsubscribe, and the grain logic always only needs to attach the processing logic. That also means that for implicit subscriptions there is never a need to Resume a subscription.
On the other hand, for explicit subscriptions, one needs to Resume the subscription, otherwise if the grain subscribes again it will result in the grain being subscribed multiple times.


**Implicit Subscriptions:**

For implicit subscriptions the grain needs to subscribe to attach the processing logic. This should be done in the grain's `OnActivateAsync` method. The grain should simply execute `await stream.SubscribeAsync(OnNext ...)` in its `OnActivateAsync` method. That will cause this particular activation to attach the `OnNext` function to process that stream. The grain can optionally specify the `StreamSequenceToken` as an argument to `SubscribeAsync`, which will cause this implicit subscription to start consuming from that token. There is never a need for implicit subscription to call `ResumeAsync`.

``` csharp
public async override Task OnActivateAsync()
{
    var streamProvider = GetStreamProvider(PROVIDER_NAME);
    var stream = streamProvider.GetStream<string>(this.GetPrimaryKey(), "MyStreamNamespace");
    await stream.SubscribeAsync(OnNextAsync)
}
```

**Explicit Subscriptions:**

For explicit subscriptions, a grain must call `SubscribeAsync` to subscribe to the stream.  This creates a subscription, as well as attaches the processing logic.
The explicit subscription will exist until the grain unsubscribes, so if a grain gets deactivated and reactivated, the grain is still explicitly subscribed, but no processing logic will be attached. In this case the grain needs to re-attach the processing logic. To do that, in its `OnActivateAsync`, the grain first needs to find out what subscriptions it has, by calling `stream.GetAllSubscriptionHandles()`. The grain must execute `ResumeAsync` on each handle it wishes to continue processing or UnsubscribeAsync on any handles it is done with. The grain can also optionally specify the `StreamSequenceToken` as an argument to the `ResumeAsync` calls, which will cause this explicit subscription to start consuming from that token.

``` csharp
public async override Task OnActivateAsync()
{
    var streamProvider = GetStreamProvider(PROVIDER_NAME);
    var stream = streamProvider.GetStream<string>(this.GetPrimaryKey(), "MyStreamNamespace");
    var subscriptionHandles = await stream.GetAllSubscriptionHandles();
    if (!subscriptionHandles.IsNullOrEmpty())
        subscriptionHandles.ForEach(async x => await x.ResumeAsync(OnNextAsync));
}
```


### Stream Order and Sequence Tokens<a name="Stream-Order-and-Sequence-Tokens"></a>

The order of event delivery between an individual producer and an individual consumer depends on the stream provider.

With SMS the producer explicitly controls the order of events seen by the consumer by controlling the way the producer publishes them. By default (if the `FireAndForget` option for SMS provider is set to false) and if the producer awaits every `OnNextAsync` call, the events arrive in FIFO order. In SMS it is up to the producer to decide how to handle delivery failures that will be indicated by a broken `Task` returned by the `OnNextAsync` call.

Azure Queue streams do not guarantee FIFO order, since the underlying Azure Queues do not guarantee order in failure cases. (They do guarantee FIFO order in failure-free executions.) When a producer produces the event into Azure Queue, if the enqueue operation fails, it is up to the producer to attempt another enqueue and later on deal with potential duplicate messages. On the delivery side, the Orleans Streaming runtime dequeues the event from the queue and attempts to deliver it for processing to consumers. The Orleans Streaming runtime deletes the event from the queue only upon successful processing. If the delivery or processing fails, the event is not deleted from the queue and will automatically re-appear in the queue later. The Streaming runtime will try to deliver it again, thus potentially breaking the FIFO order. The above behavior matches the normal semantics of Azure Queues.

**Application Defined Order**: To deal with the above ordering issues, an application can optionally specify its own ordering. This is achieved via a [**`StreamSequenceToken`**](https://github.com/dotnet/orleans/blob/master/src/Orleans.Core.Abstractions/Streams/Core/StreamSubscriptionHandle.cs), which is an opaque `IComparable` object that can be used to order events.
A producer can pass an optional `StreamSequenceToken` to the `OnNext` call. This `StreamSequenceToken` will be passed all the way to the consumer and will be delivered together with the event. That way, an application can reason and reconstruct its order independently of the streaming runtime.

### Rewindable Streams<a name="Rewindable-Streams"></a>

Some streams only allow an application to subscribe to them starting at the latest point in time, while other streams allow "going back in time". The latter capability is dependent on the underlying queuing technology and the particular stream provider. For example, Azure Queues only allow consuming the latest enqueued events, while EventHub allows replaying events from an arbitrary point in time (up to some expiration time). Streams that support going back in time are called **Rewindable Streams**.

The consumer of a rewindable stream can pass a `StreamSequenceToken` to the `SubscribeAsync` call. The runtime will deliver events to it starting from that `StreamSequenceToken`. A null token means the consumer wants to receive events starting from the latest.

The ability to rewind a stream is very useful in recovery scenarios. For example, consider a grain that subscribes to a stream and periodically checkpoints its state together with the latest sequence token. When recovering from a failure, the grain can re-subscribe to the same stream from the latest checkpointed sequence token, thereby recovering without losing any events that were generated since the last checkpoint.

[Event Hubs provider](https://www.nuget.org/packages/Microsoft.Orleans.OrleansServiceBus/) is rewindable.
You can find its code [here](https://github.com/dotnet/orleans/tree/master/src/Azure/Orleans.Streaming.EventHubs).
[SMS](https://www.nuget.org/packages/Microsoft.Orleans.OrleansProviders/) and [Azure Queue](https://www.nuget.org/packages/Microsoft.Orleans.Streaming.AzureStorage/) providers are not rewindable.

### Stateless Automatically Scaled-Out Processing<a name="Stateless-Automatically-Scaled-Out-Processing"></a>

By default Orleans Streaming is targeted to support a large number of relatively small streams, each processed by one or more stateful grains. Collectively, the processing of all the streams together is sharded among a large number of regular (stateful) grains. The application code controls this sharding by assigning stream ids and grain ids and by explicitly subscribing. **The goal is sharded stateful processing**.

However, there is also an interesting scenario of **automatically scaled-out stateless processing**. In this scenario an application has a small number of streams (or even one large stream) and the goal is stateless processing. An example is a global stream of events, where the processing involves decoding each event and potentially forwarding it to other streams for further stateful processing. The stateless scaled-out stream processing can be supported in Orleans via `StatelessWorker` grains.

**Current Status of Stateless Automatically Scaled-Out Processing:**
This is not yet implemented. An attempt to subscribe to a stream from a `StatelessWorker` grain will result in undefined behavior. [We are considering to support this option](https://github.com/dotnet/orleans/issues/433).

### Grains and Orleans Clients<a name="Grains-and-Orleans-Clients"></a>

Orleans streams work **uniformly across grains and Orleans clients**. That is, exactly the same APIs can be used inside a grain and in an Orleans client to produce and consume events. This greatly simplifies the application logic, making special client-side APIs, such as Grain Observers, redundant.


### Fully Managed and Reliable Streaming Pub-Sub<a name="Fully-Managed-and-Reliable-Streaming-Pub-Sub"></a>

To track stream subscriptions, Orleans uses a runtime component called **Streaming Pub-Sub** which serves as a rendezvous point for stream consumers and stream producers. Pub Sub tracks all stream subscriptions, persists them, and matches stream consumers with stream producers.

Applications can choose where and how the Pub-Sub data is stored. The Pub-Sub component itself is implemented as grains (called `PubSubRendezvousGrain`), which use Orleans Declarative Persistence. `PubSubRendezvousGrain` uses the storage provider named `PubSubStore`. As with any grain, you can designate an implementation for a storage provider.  For Streaming Pub-Sub you can change the implementation of the `PubSubStore` at silo construction time using the silo host builder:

The following configures Pub-Sub to store its state in Azure tables.

``` csharp
hostBuilder.AddAzureTableGrainStorage("PubSubStore", 
    options=>{ options.ConnectionString = "Secret"; });
```

That way Pub-Sub data will be durably stored in Azure Table.
For initial development you can use memory storage as well.
In addition to Pub-Sub, the Orleans Streaming Runtime delivers events from producers to consumers, manages all runtime resources allocated to actively used streams, and transparently garbage collects runtime resources from unused streams.

### Configuration<a name="Configuration"></a>

In order to use streams you need to enable stream providers via the silo host or cluster client builders. You can read more about stream providers [here](stream_providers.md). Sample stream provider setup:

``` csharp
hostBuilder.AddSimpleMessageStreamProvider("SMSProvider")
  .AddAzureQueueStreams<AzureQueueDataAdapterV2>("AzureQueueProvider",
    optionsBuilder => optionsBuilder.Configure(
      options=>{ options.ConnectionString = "Secret"; }))
  .AddAzureTableGrainStorage("PubSubStore",
    options=>{ options.ConnectionString = "Secret"; });
```

## Next

[Orleans Stream Providers](stream_providers.md)
