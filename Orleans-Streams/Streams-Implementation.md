---
layout: page
title: Orleans Streams Implementation Details
---
{% include JB/setup %}

This section provides a high level overview of Orleans Stream implementation.

*Terminology*:

We refer by the word "queue" to any durable storage technology that can injest stream events and allows to pull them from  
(or provides a push-based mechanism to consume events). Usualy, to provide scalability, those technologies provide sahrded/partitions queues.
Example are Azure Queues allow to create multipe queues, Event Hubs hubs, ... 


## Persistent Streams

All Orleans Persistent Stream Providers share a common implemenation [`PersistentStreamProvider`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/PersistentStreams/PersistentStreamProvider.cs).
This generic stream provider is parametrized with a tec hnolgy specific [`IQueueAdapter`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/QueueAdapters/IQueueAdapter.cs).

When stream producer generates a new stream item and calls `stream.OnNext()`, 
Orleans Streaming Runtime invokes the appropriate method on the `IQueueAdapter` of that stream provider that
enqueues the item directly into an appropriate queue.

At the heart of the Persistent Stream Provider are the pulling agents. 
Those pulling agents pull events from a set of durable queues deliver them to application code in grains that consumes them. 
One can think of the pulling agents as a distributed "micro-service" -- a partitioned, highly available,  
and elastic distributed component. 
The pulling agents run inside the same silos that host application grains and are fully managed by the Orleans Streaming Runtime.

### StreamQueueBalancer and StreamQueueMapper

Pulling agents are parametrized with `StreamQueueBalancerType` and `IStreamQueueMapper`. 
[`StreamQueueBalancerType`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/PersistentStreams/StreamQueueBalancerType.cs) 
expresses the way queues are balanced across Orleans silos and agents. 
The goal is to assign queues to agents in a balanced way, to prevent bottlenecks and support elasticity.
When new silo is added to the Orleans cluster, queues are automaticaly rebalanced across the old and new silos. 
StreamQueueBalancer allows to customzie that process. Orleans has a number of built in StreamQueueBalancers, 
to support different balancing scenarios (large and small number of queues) and different environments (Azure, on prem, static).

[`IStreamQueueMapper`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/QueueAdapters/IStreamQueueMapper.cs)
provides a list of all queues and is also responsible for mapping streams to queues.
That way, the producer side of the Persistent Stream Provider know which queue to enqueue the message into.

 
 

