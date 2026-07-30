---
title: Orleans streaming APIs
description: Learn about the available streaming APIs for .NET Orleans.
ms.date: 03/30/2025
ms.topic: concept-article
zone_pivot_groups: orleans-version
ms.custom: sfi-ropc-nochange
---

# Orleans streaming APIs

Applications interact with streams via APIs very similar to the well-known [Reactive Extensions (Rx) in .NET](/previous-versions/dotnet/reactive-extensions/hh242985(v=vs.103)). The main difference is that Orleans stream extensions are **asynchronous** to make processing more efficient in Orleans' distributed and scalable compute fabric.

### Async stream

You start by using a [*stream provider*](stream-providers.md) to get a handle to a stream. You can think of a stream provider as a stream factory that allows implementers to customize streams behavior and semantics:

:::zone target="docs" pivot="orleans-7-0,orleans-8-0,orleans-9-0,orleans-10-0"

:::code language="csharp" source="snippets/streaming/BasicStreaming.cs" id="get_stream_provider":::

:::zone-end
:::zone target="docs" pivot="orleans-3-x"

```csharp
IStreamProvider streamProvider = base.GetStreamProvider("SimpleStreamProvider");
IAsyncStream<T> stream = streamProvider.GetStream<T>(Guid, "MyStreamNamespace");
```

:::zone-end

You can get a reference to the stream provider either by calling the <xref:Orleans.Grain.GetStreamProvider*?displayProperty=nameWithType> method when inside a grain or by calling the <xref:Orleans.GrainStreamingExtensions.GetStreamProvider*> method on the client instance.

<xref:Orleans.Streams.IAsyncStream`1?displayProperty=nameWithType> is a logical, strongly typed handle to a virtual stream, similar in spirit to an Orleans Grain Reference. Calls to <xref:Orleans.GrainStreamingExtensions.GetStreamProvider*> and `GetStream` are purely local. The arguments to `GetStream` are a GUID and an additional string called a stream namespace (which can be null). Together, the GUID and the namespace string comprise the stream identity (similar to the arguments for <xref:Orleans.IGrainFactory.GetGrain*?displayProperty=nameWithType>). This combination provides extra flexibility in determining stream identities. Just like grain 7 might exist within the `PlayerGrain` type and a different grain 7 might exist within the `ChatRoomGrain` type, Stream 123 can exist within the `PlayerEventsStream` namespace, and a different stream 123 can exist within the `ChatRoomMessagesStream` namespace.

### Producing and consuming

<xref:Orleans.Streams.IAsyncStream`1> implements both the <xref:Orleans.Streams.IAsyncObserver`1> and <xref:Orleans.Streams.IAsyncObservable`1> interfaces. This allows your application to use the stream either to produce new events using <xref:Orleans.Streams.IAsyncObserver`1> or to subscribe to and consume events using `IAsyncObservable<T>`.

```csharp
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

To produce events into the stream, your application calls:

```csharp
await stream.OnNextAsync<T>(event)
```

To subscribe to a stream, your application calls:

```csharp
StreamSubscriptionHandle<T> subscriptionHandle = await stream.SubscribeAsync(IAsyncObserver)
```

The argument to <xref:Orleans.Streams.AsyncObservableExtensions.SubscribeAsync*> can be either an object implementing the <xref:Orleans.Streams.IAsyncObserver`1> interface or a combination of lambda functions to process incoming events. More options for <xref:Orleans.Streams.IAsyncObservable`1.SubscribeAsync*> are available via the <xref:Orleans.Streams.AsyncObservableExtensions> class. <xref:Orleans.Streams.IAsyncObservable`1.SubscribeAsync*> returns a <xref:Orleans.Streams.StreamSubscriptionHandle`1>, an opaque handle used to unsubscribe from the stream (similar to an asynchronous version of <xref:System.IDisposable>).

```csharp
await subscriptionHandle.UnsubscribeAsync()
```

It's important to note that the subscription is for a grain, not for activation. Once the grain code subscribes to the stream, this subscription surpasses the life of this activation and remains durable forever until the grain code (potentially in a different activation) explicitly unsubscribes. This is the core of the virtual stream abstraction: not only do all streams always exist logically, but a stream subscription is also durable and lives beyond the particular physical activation that created it.

### Multiplicity

An Orleans stream can have multiple producers and multiple consumers. A message published by a producer is delivered to all consumers subscribed to the stream before the message was published.

Additionally, a consumer can subscribe to the same stream multiple times. Each time it subscribes, it gets back a unique <xref:Orleans.Streams.StreamSubscriptionHandle`1>. If a grain (or client) subscribes X times to the same stream, it receives the same event X times, once for each subscription. The consumer can also unsubscribe from an individual subscription. You can find all current subscriptions by calling:

```csharp
IList<StreamSubscriptionHandle<T>> allMyHandles =
    await IAsyncStream<T>.GetAllSubscriptionHandles();
```

### Recovering from failures

If the producer of a stream dies (or its grain is deactivated), it doesn't need to do anything. The next time this grain wants to produce more events, it can get the stream handle again and produce new events as usual.

Consumer logic is slightly more involved. As mentioned before, once a consumer grain subscribes to a stream, this subscription is valid until the grain explicitly unsubscribes. If the consumer of the stream dies (or its grain is deactivated) and a new event is generated on the stream, the consumer grain automatically reactivates (just like any regular Orleans grain automatically activates when a message is sent to it). The only thing the grain code needs to do now is provide an <xref:Orleans.Streams.IAsyncObserver`1> to process the data. The consumer needs to re-attach processing logic as part of the <xref:Orleans.Grain.OnActivateAsync> method. To do that, it can call:

```csharp
StreamSubscriptionHandle<int> newHandle =
    await subscriptionHandle.ResumeAsync(IAsyncObserver);
```

The consumer uses the previous handle obtained during the initial subscription to "resume processing". Note that <xref:Orleans.Streams.StreamSubscriptionHandle`1.ResumeAsync*> merely updates an existing subscription with the new instance of <xref:Orleans.Streams.IAsyncObserver`1> logic and doesn't change the fact that this consumer is already subscribed to this stream.

How does the consumer get the old `subscriptionHandle`? There are two options. The consumer might have persisted the handle returned from the original <xref:Orleans.Streams.IAsyncObservable`1.SubscribeAsync*> operation and can use it now. Alternatively, if the consumer doesn't have the handle, it can ask the <xref:Orleans.Streams.IAsyncStream`1> for all its active subscription handles by calling:

```csharp
IList<StreamSubscriptionHandle<T>> allMyHandles =
    await IAsyncStream<T>.GetAllSubscriptionHandles();
```

The consumer can then resume all of them or unsubscribe from some if desired.

> [!TIP]
> If the consumer grain implements the <xref:Orleans.Streams.IAsyncObserver`1> interface directly (`public class MyGrain<T> : Grain, IAsyncObserver<T>`), it shouldn't theoretically need to re-attach the <xref:Orleans.Streams.IAsyncObserver`1> and thus wouldn't need to call `ResumeAsync`. The streaming runtime should automatically figure out that the grain already implements <xref:Orleans.Streams.IAsyncObserver`1> and invoke those <xref:Orleans.Streams.IAsyncObserver`1> methods. However, the streaming runtime currently doesn't support this, and the grain code still needs to explicitly call `ResumeAsync`, even if the grain implements <xref:Orleans.Streams.IAsyncObserver`1> directly.

### Explicit and implicit subscriptions

By default, a stream consumer must explicitly subscribe to the stream. This subscription is usually triggered by an external message the grain (or client) receives instructing it to subscribe. For example, in a chat service, when a user joins a chat room, their grain receives a `JoinChatGroup` message with the chat name, causing the user grain to subscribe to this chat stream.

Additionally, Orleans streams support *implicit subscriptions*. In this model, the grain doesn't explicitly subscribe. It's subscribed automatically and implicitly based on its grain identity and an <xref:Orleans.ImplicitStreamSubscriptionAttribute>. The main value of implicit subscriptions is allowing stream activity to trigger grain activation (and thus the subscription) automatically. For example, using SMS streams, if one grain wanted to produce a stream and another grain process it, the producer would need the consumer grain's identity and make a grain call telling it to subscribe. Only then could it start sending events. Instead, with implicit subscriptions, the producer can just start producing events to a stream, and the consumer grain automatically activates and subscribes. In this case, the producer doesn't need to know who is reading the events.

The grain implementation `MyGrainType` can declare an attribute `[ImplicitStreamSubscription("MyStreamNamespace")]`. This tells the streaming runtime that when an event is generated on a stream with identity GUID XXX and namespace `"MyStreamNamespace"`, it should be delivered to the grain with identity XXX of type `MyGrainType`. That is, the runtime maps stream `<XXX, MyStreamNamespace>` to consumer grain `<XXX, MyGrainType>`.

The presence of `ImplicitStreamSubscription` causes the streaming runtime to automatically subscribe this grain to the stream and deliver stream events to it. However, the grain code still needs to tell the runtime how it wants events processed. Essentially, it needs to attach the <xref:Orleans.Streams.IAsyncObserver`1>. Therefore, when the grain activates, the grain code inside <xref:Orleans.Grain.OnActivateAsync*> needs to call:

:::zone target="docs" pivot="orleans-7-0,orleans-8-0,orleans-9-0,orleans-10-0"

:::code language="csharp" source="snippets/streaming/ImplicitSubscriptions.cs" id="implicit_subscription_setup":::

:::zone-end
:::zone target="docs" pivot="orleans-3-x"

```csharp
IStreamProvider streamProvider =
    base.GetStreamProvider("SimpleStreamProvider");

IAsyncStream<T> stream =
    streamProvider.GetStream<T>(this.GetPrimaryKey(), "MyStreamNamespace");

StreamSubscriptionHandle<T> subscription =
    await stream.SubscribeAsync(IAsyncObserver<T>);
```

:::zone-end

### Writing subscription logic

Below are guidelines for writing subscription logic for various cases: explicit and implicit subscriptions, rewindable and non-rewindable streams. The main difference between explicit and implicit subscriptions is that for implicit subscriptions, the grain always has exactly one implicit subscription per stream namespace. There's no way to create multiple subscriptions (no subscription multiplicity), no way to unsubscribe, and the grain logic only ever needs to attach the processing logic. This also means there's never a need to resume an implicit subscription. On the other hand, for explicit subscriptions, you need to resume the subscription; otherwise, subscribing again results in the grain being subscribed multiple times.

**Implicit subscriptions:**

For implicit subscriptions, the grain still needs to subscribe to attach the processing logic. You can do this in the consumer grain by implementing the `IStreamSubscriptionObserver` and <xref:Orleans.Streams.IAsyncObserver`1> interfaces, allowing the grain to activate separately from subscribing. To subscribe to the stream, the grain creates a handle and calls `await handle.ResumeAsync(this)` in its `OnSubscribed(...)` method.

To process messages, implement the `IAsyncObserver<T>.OnNextAsync(...)` method to receive stream data and a sequence token. Alternatively, the `ResumeAsync` method can take a set of delegates representing the methods of the <xref:Orleans.Streams.IAsyncObserver`1> interface: `onNextAsync`, `onErrorAsync`, and `onCompletedAsync`.

:::zone target="docs" pivot="orleans-7-0,orleans-8-0,orleans-9-0,orleans-10-0"

:::code language="csharp" source="snippets/streaming/ImplicitSubscriptions.cs" id="on_next_async":::

:::code language="csharp" source="snippets/streaming/ImplicitSubscriptions.cs" id="on_subscribed":::

:::zone-end

:::zone target="docs" pivot="orleans-3-x"

```csharp
public override async Task OnActivateAsync()
{
    var streamProvider = this.GetStreamProvider(PROVIDER_NAME);
    var stream =
        streamProvider.GetStream<string>(
            this.GetPrimaryKey(), "MyStreamNamespace");

    await stream.SubscribeAsync(OnNextAsync);
}
```

:::zone-end

**Explicit subscriptions:**

For explicit subscriptions, a grain must call <xref:Orleans.Streams.IAsyncObservable`1.SubscribeAsync*> to subscribe to the stream. This creates a subscription and attaches the processing logic. The explicit subscription exists until the grain unsubscribes. If a grain deactivates and reactivates, it's still explicitly subscribed, but no processing logic is attached. In this case, the grain needs to re-attach the processing logic. To do this, in its <xref:Orleans.Grain.OnActivateAsync*>, the grain first needs to find out its subscriptions by calling <xref:Orleans.Streams.IAsyncStream`1.GetAllSubscriptionHandles?displayProperty=nameWithType>. The grain must execute `ResumeAsync` on each handle it wishes to continue processing or `UnsubscribeAsync` on any handles it's done with. The grain can also optionally specify the <xref:Orleans.Streams.StreamSequenceToken> as an argument to the `ResumeAsync` calls, causing this explicit subscription to start consuming from that token.

:::zone target="docs" pivot="orleans-7-0,orleans-8-0,orleans-9-0,orleans-10-0"

:::code language="csharp" source="snippets/streaming/ExplicitSubscriptions.cs" id="explicit_subscription_activate":::

:::zone-end
:::zone target="docs" pivot="orleans-3-x"

```csharp
public async override Task OnActivateAsync()
{
    var streamProvider = this.GetStreamProvider(PROVIDER_NAME);
    var stream =
        streamProvider.GetStream<string>(this.GetPrimaryKey(), "MyStreamNamespace");

    var subscriptionHandles = await stream.GetAllSubscriptionHandles();
    if (!subscriptionHandles.IsNullOrEmpty())
    {
        subscriptionHandles.ForEach(
            async x => await x.ResumeAsync(OnNextAsync));
    }
}
```

:::zone-end

### Stream order and sequence tokens

The order of event delivery between an individual producer and consumer depends on the stream provider.

With SMS, the producer explicitly controls the order of events seen by the consumer by controlling how they publish them. By default (if the <xref:Orleans.Configuration.SimpleMessageStreamProviderOptions.FireAndForgetDelivery?displayProperty=nameWithType> option for the SMS provider is `false`) and if the producer awaits every <xref:Orleans.Streams.IAsyncObserver`1.OnNextAsync*> call, events arrive in FIFO order. In SMS, the producer decides how to handle delivery failures indicated by a broken <xref:System.Threading.Tasks.Task> returned by the <xref:Orleans.Streams.IAsyncObserver`1.OnNextAsync*> call.

Azure Queue streams don't guarantee FIFO order because the underlying Azure Queues don't guarantee order in failure cases (though they do guarantee FIFO order in failure-free executions). When a producer produces an event into an Azure Queue, if the queue operation fails, the producer must attempt another queue and later deal with potential duplicate messages. On the delivery side, the Orleans Streaming runtime dequeues the event and attempts to deliver it for processing to consumers. The runtime deletes the event from the queue only upon successful processing. If delivery or processing fails, the event isn't deleted from the queue and automatically reappears later. The Streaming runtime tries to deliver it again, potentially breaking FIFO order. This behavior matches the normal semantics of Azure Queues.

**Application-defined order**: To handle the ordering issues above, your application can optionally specify its ordering. Achieve this using a <xref:Orleans.Streams.StreamSequenceToken>, an opaque <xref:System.IComparable> object used to order events. A producer can pass an optional <xref:Orleans.Streams.StreamSequenceToken> to the <xref:Orleans.Streams.IAsyncObserver`1.OnNextAsync*> call. This <xref:Orleans.Streams.StreamSequenceToken> passes to the consumer and is delivered with the event. This way, your application can reason about and reconstruct its order independently of the streaming runtime.

### Rewindable streams

Some streams only allow subscribing starting from the latest point in time, while others allow "going back in time." This capability depends on the underlying queuing technology and the specific stream provider. For example, Azure Queues only allow consuming the latest enqueued events, while Event Hubs allows replaying events from an arbitrary point in time (up to some expiration time). Streams supporting going back in time are called *rewindable streams*.

The consumer of a rewindable stream can pass a <xref:Orleans.Streams.StreamSequenceToken> to the <xref:Orleans.Streams.IAsyncObservable`1.SubscribeAsync*> call. The runtime delivers events starting from that <xref:Orleans.Streams.StreamSequenceToken>. A null token means the consumer wants to receive events starting from the latest.

The ability to rewind a stream is very useful in recovery scenarios. For example, consider a grain that subscribes to a stream and periodically checkpoints its state along with the latest sequence token. When recovering from a failure, the grain can re-subscribe to the same stream from the latest checkpointed sequence token, recovering without losing any events generated since the last checkpoint.

The [Event Hubs provider](https://www.nuget.org/packages/Microsoft.Orleans.Streaming.EventHubs) is rewindable. You can find its code on [GitHub: Orleans/Azure/Orleans.Streaming.EventHubs](https://github.com/dotnet/orleans/tree/main/src/Azure/Orleans.Streaming.EventHubs). The SMS (now [Broadcast Channel](broadcast-channel.md)) and [Azure Queue](https://www.nuget.org/packages/Microsoft.Orleans.Streaming.AzureStorage) providers are _not_ rewindable.

### Stateless automatically scaled-out processing

By default, Orleans Streaming targets supporting a large number of relatively small streams, each processed by one or more stateful grains. Collectively, the processing of all streams is sharded among many regular (stateful) grains. Your application code controls this sharding by assigning stream IDs and grain IDs and by explicitly subscribing. The goal is sharded stateful processing.

However, there's also an interesting scenario of automatically scaled-out stateless processing. In this scenario, an application has a small number of streams (or even one large stream), and the goal is stateless processing. An example is a global stream of events where processing involves decoding each event and potentially forwarding it to other streams for further stateful processing. Stateless scaled-out stream processing can be supported in Orleans via <xref:Orleans.Concurrency.StatelessWorkerAttribute> grains.

**Current status of stateless automatically scaled-out processing:**
This isn't yet implemented. Attempting to subscribe to a stream from a <xref:Orleans.Concurrency.StatelessWorkerAttribute> grain results in undefined behavior. [We are considering supporting this option](https://github.com/dotnet/orleans/issues/433).

### Grains and Orleans clients

Orleans streams work uniformly across grains and Orleans clients. That means you can use the same APIs inside a grain and in an Orleans client to produce and consume events. This greatly simplifies application logic, making special client-side APIs like Grain Observers redundant.

### Fully managed and reliable streaming pub-sub

To track stream subscriptions, Orleans uses a runtime component called **Streaming Pub-Sub**, which serves as a rendezvous point for stream consumers and producers. Pub-sub tracks all stream subscriptions, persists them, and matches stream consumers with stream producers.

Applications can choose where and how the Pub-Sub data is stored. The Pub-Sub component itself is implemented as grains (called `PubSubRendezvousGrain`), which use Orleans declarative persistence. `PubSubRendezvousGrain` uses the storage provider named `PubSubStore`. As with any grain, you can designate an implementation for a storage provider. For Streaming Pub-Sub, you can change the implementation of the `PubSubStore` at silo construction time using the silo host builder:

The following configures Pub-Sub to store its state in Azure tables.

:::zone target="docs" pivot="orleans-7-0,orleans-8-0,orleans-9-0,orleans-10-0"

### [Managed identity (recommended)](#tab/managed-identity)

:::code language="csharp" source="snippets/streaming/Configuration.cs" id="pubsub_managed_identity":::

### [Connection string](#tab/connection-string)

:::code language="csharp" source="snippets/streaming/Configuration.cs" id="pubsub_connection_string":::

---

:::zone-end

:::zone target="docs" pivot="orleans-3-x"

```csharp
hostBuilder.AddAzureTableGrainStorage("PubSubStore",
    options => options.ConnectionString = "<Secret>");
```

:::zone-end

This way, Pub-Sub data is durably stored in Azure Table. For initial development, you can use memory storage as well. In addition to Pub-Sub, the Orleans Streaming Runtime delivers events from producers to consumers, manages all runtime resources allocated to actively used streams, and transparently garbage collects runtime resources from unused streams.

### Configuration

To use streams, you need to enable [stream providers](stream-providers.md) via the silo host or cluster client builders. Sample stream provider setup:

:::zone target="docs" pivot="orleans-7-0,orleans-8-0,orleans-9-0,orleans-10-0"

### [Managed identity (recommended)](#tab/managed-identity)

:::code language="csharp" source="snippets/streaming/Configuration.cs" id="stream_provider_managed_identity":::

### [Connection string](#tab/connection-string)

:::code language="csharp" source="snippets/streaming/Configuration.cs" id="stream_provider_connection_string":::

---

:::zone-end

:::zone target="docs" pivot="orleans-3-x"

```csharp
hostBuilder.AddSimpleMessageStreamProvider("SMSProvider")
    .AddAzureQueueStreams<AzureQueueDataAdapterV2>("AzureQueueProvider",
        optionsBuilder => optionsBuilder.Configure(
            options => options.ConnectionString = "<Secret>"))
    .AddAzureTableGrainStorage("PubSubStore",
        options => options.ConnectionString = "<Secret>");
```

:::zone-end

## See also

[Orleans Stream Providers](stream-providers.md)
