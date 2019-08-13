---
layout: page
title: Orleans Stream Providers
---


# Stream Providers

Streams can come in different shapes and forms.
Some streams may deliver events over direct TCP links, while others deliver events via durable queues.
Different stream types may use different batching strategies, different caching algorithms, or different back pressure procedures.
To avoid constraining streaming applications to only a subset of those behavioral choices, **Stream Providers** are extensibility points to Orleans Streaming Runtime that allow users to implement any type of stream.
This extensibility point is similar in spirit to [Orleans Storage Providers](https://github.com/dotnet/orleans/wiki/Custom%20Storage%20Providers).
Orleans currently ships with many stream providers, including : [Simple Message Stream Provider](https://github.com/dotnet/orleans/blob/master/src/Orleans.Core/Streams/SimpleMessageStream/SimpleMessageStreamProvider.cs) and [Azure Queue Stream Provider](https://github.com/dotnet/orleans/tree/master/src/Azure/Orleans.Streaming.AzureStorage/Providers/Streams/AzureQueue).

## Simple Message Stream Provider

Simple Message Stream Provider, also known as the SMS provider, delivers events over TCP by utilizing regular Orleans grain messaging.
Since events in SMS are delivered over unreliable TCP links, SMS does _not_ guarantee reliable event delivery and does not automatically resend failed messages for SMS streams.
The producer of the SMS stream has a way to know if its event was successfully received and processed or not: by default the call to `stream.OnNextAsync` returns a `Task` that represents the processing status of the stream consumer.
If this Task fails, the producer can decide to send the same event again, thus achieving reliability on the application level.
Although individual stream messages delivery is best effort, SMS streams themselves are reliable.
That is, the subscriber-to-producer binding performed by Pub Sub is fully reliable.

## Azure Queue (AQ) Stream Provider

Azure Queue (AQ) Stream Provider delivers events over Azure Queues.
On the producer side, AQ Stream Provider enqueues events directly into Azure Queue.
On the consumer side, AQ Stream Provider manages a set of **pulling agents** that pull events from a set of Azure Queues and deliver them to application code that consumes them.
One can think of the pulling agents as a distributed "micro-service" -- a partitioned, highly available, and elastic distributed component.
The pulling agents run inside the same silos that host application grains.
Thus, there is no need to run separate Azure worker roles to pull from the queues.
The existence of pulling agents, their management, backpressure, balancing the queues between them, and handing off queues from a failed agent to another agent are fully managed by Orleans Streaming Runtime and are transparent to application code that uses streams.

## Queue Adapters

Different stream providers that deliver events over durable queues exhibit similar behavior and are subject to a similar implementation.
Therefore, we provide a generic extensible [`PersistentStreamProvider`](https://github.com/dotnet/orleans/blob/master/src/Orleans.Core/Streams/PersistentStreams/PersistentStreamProvider.cs) that allows developers to plug in different types of queues without writing a completely new stream provider from scratch.
`PersistentStreamProvider` uses an [`IQueueAdapter`](https://github.com/dotnet/orleans/blob/master/src/Orleans.Core/Streams/QueueAdapters/IQueueAdapter.cs) component, which abstracts specific queue implementation details and provides means to enqueue and dequeue events.
All the rest is handled by the logic inside the `PersistentStreamProvider`.
Azure Queue Provider mentioned above is also implemented this way: it is an instance of `PersistentStreamProvider` that uses an `AzureQueueAdapter`.

## Next

[Orleans Streams Implementation Details](../implementation/streams_implementation.md)
