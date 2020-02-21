---
layout: page
title: Streams Implementation Details
---

# Orleans Streams Implementation Details

This section provides a high level overview of Orleans Stream implementation.
It describes concepts and details that are not visible on the application level.
If you only plan to use streams, you do not have to read this section.

*Terminology*:

We refer by the word "queue" to any durable storage technology that can ingest stream events and allows either to pull events or provides a push-based mechanism to consume events.
Usually, to provide scalability, those technologies provide sharded/partitioned queues.
For example, Azure Queues allow to create multiple queues, Event Hubs have multiple hubs, Kafka topics, ...


## Persistent Streams<a name="Persistent-Streams"></a>

All Orleans Persistent Stream Providers share a common implementation [**`PersistentStreamProvider`**](https://github.com/dotnet/orleans/blob/master/src/Orleans.Core/Streams/PersistentStreams/PersistentStreamProvider.cs).
This generic stream provider needs be configured with a technology specific [**`IQueueAdapterFactory`**](https://github.com/dotnet/orleans/blob/master/src/Orleans.Core/Streams/QueueAdapters/IQueueAdapterFactory.cs).

For instance, for testing purposes we have queue adapters that generate their own test data rather than reading the data from a queue.
The code below shows how we configure a persistent stream provider to use our custom (generator) queue adapter.
It does this by configuring the persistent stream provider with a factory function used to create the adapter.

``` csharp
hostBuilder.AddPersistentStreams(StreamProviderName, GeneratorAdapterFactory.Create);
```

When a stream producer generates a new stream item and calls `stream.OnNext()`, the Orleans Streaming Runtime invokes the appropriate method on the `IQueueAdapter` of that stream provider which enqueues the item directly onto the appropriate queue.

### Pulling Agents<a name="Pulling-Agents"></a>

At the heart of the Persistent Stream Provider are the pulling agents.
Pulling agents pull events from a set of durable queues and deliver them to the application code in grains that consumes them.
One can think of the pulling agents as a distributed "micro-service" -- a partitioned, highly available, and elastic distributed component.
The pulling agents run inside the same silos that host application grains and are fully managed by the Orleans Streaming Runtime.

### StreamQueueMapper and StreamQueueBalancer<a name="StreamQueueMapper-and-StreamQueueBalancer"></a>

Pulling agents are parameterized with `IStreamQueueMapper` and `IStreamQueueBalancer`.
[**`IStreamQueueMapper`**](https://github.com/dotnet/orleans/blob/master/src/Orleans.Core/Streams/QueueAdapters/IStreamQueueMapper.cs)
provides a list of all queues and is also responsible for mapping streams to queues.
That way, the producer side of the Persistent Stream Provider knows into which queue to enqueue the message.

[**`IStreamQueueBalancer`**](https://github.com/dotnet/orleans/blob/master/src/Orleans.Core/Streams/PersistentStreams/IStreamQueueBalancer.cs)
expresses the way queues are balanced across Orleans silos and agents.
The goal is to assign queues to agents in a balanced way, to prevent bottlenecks and support elasticity.
When a new silo is added to the Orleans cluster, queues are automatically rebalanced across the old and new silos.
StreamQueueBalancer allows customizing that process. Orleans has a number of built-in StreamQueueBalancers, to support different balancing scenarios (large and small number of queues) and different environments (Azure, on prem, static).

Using the test generator example from above, the code below shows how one could configure the queue mapper and queue balancer.

``` csharp
hostBuilder
  .AddPersistentStreams(StreamProviderName, GeneratorAdapterFactory.Create,
    providerConfigurator=>providerConfigurator
      .Configure<HashRingStreamQueueMapperOptions>(ob=>ob.Configure(
        options=>{ options.TotalQueueCount = 8; }))
      .UseDynamicClusterConfigDeploymentBalancer()
);
```

The above code configures the GeneratorAdapter to use a queue mapper with 8 queues, and balances the queues across the cluster using the `DynamicClusterConfigDeploymentBalancer`.

### Pulling Protocol<a name="Pulling-Protocol"></a>

Every silo runs a set of pulling agents, every agent is pulling from one queue. Pulling agents themselves are implemented by an internal runtime component, called **SystemTarget**.
SystemTargets are essentially runtime grains, are subject to single threaded concurrency, can use regular grain messaging, and are as lightweight as grains.
In contrast to grains, SystemTargets are not virtual: they are explicitly created (by the runtime) and are not location transparent.
By implementing pulling agents as SystemTargets, the Orleans Streaming Runtime can rely on built-in Orleans features and can scale to a very large number of queues, since creating a new pulling agent is as cheap as creating a new grain.

Every pulling agent runs a periodic timer that pulls from the queue (by invoking [**`IQueueAdapterReceiver`**](https://github.com/dotnet/orleans/blob/master/src/Orleans.Core/Streams/QueueAdapters/IQueueAdapterReceiver.cs)) `GetQueueMessagesAsync()` method. The returned messages are put in the internal per-agent data structure called `IQueueCache`.
Every message is inspected to find out its destination stream.
The agent uses the Pub Sub to find out the list of stream consumers that subscribed to this stream. Once the consumer list is retrieved, the agent stores it locally (in its pub-sub cache) so it does not need to consult with Pub Sub on every message.
The agent also subscribes to the pub-sub to receive notification of any new consumers that subscribe to that stream.
This handshake between the agent and the pub-sub guarantees **strong streaming subscription semantics**: *once the consumer has subscribed to the stream it will see all events that were generated after it has subscribed*. In addition, using `StreamSequenceToken` allows it to subscribe in the past.

### Queue Cache<a name="Queue-Cache"></a>

[**`IQueueCache`**](https://github.com/dotnet/orleans/blob/master/src/Orleans.Core/Streams/QueueAdapters/IQueueCache.cs) is an internal per-agent data structure that allows to decoupling dequeuing new events from the queue and delivering them to consumers.
It also allows to decoupling delivery to different streams and to different consumers.

Imagine a situation where one stream has 3 stream consumers and one of them is slow.
If care is not taken, it is possible that this slow consumer will impact the agent's progress, slowing the consumption of other consumers of that stream, and even slowing the dequeuing and delivery of events for other streams.
To prevent that and allow maximum parallelism in the agent, we use `IQueueCache`.

`IQueueCache` buffers stream events and provides a way for the agent to deliver events to each consumer at its own pace.
The per-consumer delivery is implemented by the internal component called `IQueueCacheCursor`, which tracks per-consumer progress.
That way, each consumer receives events at its own pace: fast consumers receive events as quickly as they are dequeued from the queue, while slow consumers receive them later on.
Once the message is delivered to all consumers, it can be deleted from the cache.

### Backpressure<a name="Backpressure"></a>

Backpressure in the Orleans Streaming Runtime applies in two places: **bringing stream events from the queue to the agent** and **delivering the events from the agent to stream consumers**.

The latter is provided by the built-in Orleans message delivery mechanism. Every stream event is delivered from the agent to consumers via the standard Orleans grain messaging, one at a time.
That is, the agents sends one event (or a limited size batch of events) to each individual stream consumer and awaits this call.
The next event will not start being delivered until the Task for the previous event was resolved or broken. That way we naturally limit the per-consumer delivery rate to one message at a time.

With regard to bringing stream events from the queue to the agent, Orleans Streaming provides a new special Backpressure mechanism.
Since the agent decouples dequeuing of events from the queue and delivering them to consumers, it is possible that a single slow consumer will fall behind so much that the `IQueueCache` will fill up. 
To prevent `IQueueCache` from growing indefinitely, we limit its size (the size limit is configurable). However, the agent never throws away undelivered events.
Instead, when the cache starts to fill up, the agents slow the rate of dequeuing events from the queue. That way, we can "ride out" the slow delivery periods by adjusting the rate at which we consume from the queue ("backpressure") and get back into fast consumption rate later on. To detect the "slow delivery" valleys the `IQueueCache` uses an internal data structure of cache buckets that tracks the progress of delivery of events to individual stream consumers. This results in a very responsive and self-adjusting system.
