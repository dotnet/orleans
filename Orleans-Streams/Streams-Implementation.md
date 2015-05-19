---
layout: page
title: Orleans Streams Implementation Details
---
{% include JB/setup %}

This section provides a high level overview of Orleans Stream implementation.

*Terminology*:

We refer by the word "queue" to any durable storage technology that can ingest stream events and allows either to pull events or provides a push-based mechanism to consume events. Usualy, to provide scalability, those technologies provide sharded/partitions queues. Example are Azure Queues allow to create multipe queues, Event Hubs hubs, ... 


## Persistent Streams

All Orleans Persistent Stream Providers share a common implemenation [**`PersistentStreamProvider`**](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/PersistentStreams/PersistentStreamProvider.cs).
This generic stream provider is parametrized with a technology specific [**`IQueueAdapter`**](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/QueueAdapters/IQueueAdapter.cs).

When stream producer generates a new stream item and calls `stream.OnNext()`, 
Orleans Streaming Runtime invokes the appropriate method on the `IQueueAdapter` of that stream provider that
enqueues the item directly into an appropriate queue.

### Pulling Agents

At the heart of the Persistent Stream Provider are the pulling agents. 
Pulling agents pull events from a set of durable queues and deliver them to the application code in grains that consumes them.  One can think of the pulling agents as a distributed "micro-service" -- a partitioned, highly available, and elastic distributed component. 
The pulling agents run inside the same silos that host application grains and are fully managed by the Orleans Streaming Runtime.

### StreamQueueMapper and StreamQueueBalancer

Pulling agents are parametrized with `IStreamQueueMapper` and `StreamQueueBalancerType`. 

[**`IStreamQueueMapper`**](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/QueueAdapters/IStreamQueueMapper.cs)
provides a list of all queues and is also responsible for mapping streams to queues.
That way, the producer side of the Persistent Stream Provider know which queue to enqueue the message into.

[**`StreamQueueBalancerType`**](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/PersistentStreams/StreamQueueBalancerType.cs) 
expresses the way queues are balanced across Orleans silos and agents. 
The goal is to assign queues to agents in a balanced way, to prevent bottlenecks and support elasticity.
When new silo is added to the Orleans cluster, queues are automaticaly rebalanced across the old and new silos. 
StreamQueueBalancer allows to customize that process. Orleans has a number of built in StreamQueueBalancers, 
to support different balancing scenarios (large and small number of queues) and different environments (Azure, on prem, static).

### Pulling Protocol

Every silo runs a set of pulling agents, every agent is pulling from one queue. Pulling agents themselves are implemented by the internal runtime component, called **SystemTarget**. SystemTargets are essentially runtime grains, are subject to single threaded concurency, can use regular grain messaging and are as lightweight as grains. As opposite to grain, SystemTargets are not virtual: they are explicitely created (by the runtime) and are also not location transparrent. By implementing pulling agents as SystemTargets Orleans Streaming Runtime can rely on a lot of built-in Orleans features and can also scale to a very large number of queues, since creating a new pulling agent is as cheap as creating a new grain.

Every pulling agent runs periodic timer that pulls from the queue (by invocking [**`IQueueAdapterReceiver`**](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/QueueAdapters/IQueueAdapterReceiver.cs)) `GetQueueMessagesAsync()` method. The returned messages are put in the internal per-agent data structure called `IQueueCache`. Every message is inspected to find out its destinatin stream. The agent uses the Pub Sub to find out the list of stream consumers that subsribed to this stream. Once the consumer list if retrived, the agent stores it locally (in its pub-sub cache) so it doe snot need to consuklt with Pub Sub on every message. The agent also subsribes with the pub-sub to rceive notification of any new consumers that subsribe to that stream.
This handshake between the agent and the pub-sub guarantees **strong streaming subsription semantics**: *once the consumer has subscribed to the stream it will see all events that were generated after it has subscribed* (in addition, using `StreamSequenceToken` allows to subscribe in the past).


### Queue Cache

[**`IQueueCache`**](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/QueueAdapters/IQueueCache.cs) is an internal per-agent data structure that allows to decouple bringing new events from the queue from delivering them to consumers. It also allowsto decouple delivery to different streams and to different consumers.

Imagine a situation when one stream has 3 stream consumers and one of them is slow. If care not taken, it is possible that this slow consumer will impact agent's progress, slowing the consumption of other consumers of that stream, and even potentially slowing the dequieng and deliverying of events for other stream. To prevent that and allow maximum parallelism in the agent, we use `IQueueCache`.

`IQueueCache` buffers stream events and provides a way to the agent to deliver events to each consumer at its pace. The per-consumer delivery is implemented by the internal component called `IQueueCacheCursor`, which tracks per consumer progress. That way each consumer receives events at its own pace :fast consumers receive events as quickly as they are dequeueded from the queue, while slow consumers recieve them later on. Once the msesage was delivered to all consumers, it can be deleted from the cache.

### Backpressure

Backpressure in Orleans Streaming Runtime applies in two places: **bringing stream events from the queue to the agent** and **deliverying the events from the agent to stream consumers**.

The latter is provided by the built-in Orleans messaging delivery mechanism. Every stream event is delivered from the agent to consumers via the standard Orleans grain messaging, one at a time. That is, the agenst sends one event (or a limited size batch of events) to each individual stream consumer and awaits this call. The next event will not start being delivered until the Task for the previous event was resolved or broken. That way we naturally limit theper-consumer delivery rate to one message at a time.

With regard to bringing stream events from the queue to the agent Orleans Streaming provides a new special Backpressure mechanism. Since the agent decouples dequeuing of events from the queue and deliverying them to consumers, it is possible that a single slow consumer will fall behind so much that the `IQueueCache` will fill up. To prevent `IQueueCache` from growing indefinitely, we limit it's size (the size limit is configurable). However, the agent never throws away undelivered events. Instead, when the cache starts to fill up, the agents slows the rate of dequeing events from the queue. That way, we can "ride" the slow delivery periods by adjusting the rate at which we consume from the queue ("backpressure") and get back into fast consumption rate later on. To detect the "slow delivery" valleys the `IQueueCache` uses an internal data structure of cache buckets that track the progress of delivery of events to individual stream consumer. This results in a very responsive and self-adjusting systems.

