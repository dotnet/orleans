---
title: Why streams in Orleans?
description: Learn why you'd want to use streams in .NET Orleans.
ms.date: 03/30/2025
ms.topic: concept-article
---

# Why streams in Orleans?

A wide range of technologies already exists for building stream processing systems. These include systems to **durably store stream data** (for example, [Event Hubs](https://azure.microsoft.com/services/event-hubs/) and [Kafka](https://kafka.apache.org/)) and systems to express **compute operations** over stream data (for example, [Azure Stream Analytics](https://azure.microsoft.com/services/stream-analytics/), [Apache Storm](https://storm.apache.org/), and [Apache Spark Streaming](https://spark.apache.org/streaming/)). These are great systems that allow you to build efficient data stream processing pipelines.

### Limitations of existing systems

However, these systems aren't suitable for **fine-grained free-form compute over stream data**. The streaming compute systems mentioned above all allow you to specify a **unified data-flow graph of operations applied in the same way to all stream items**. This is a powerful model when data is uniform and you want to express the same set of transformation, filtering, or aggregation operations over this data. But other use cases require expressing fundamentally different operations over different data items. In some of these cases, as part of the processing, you might occasionally need to make an external call, such as invoking an arbitrary REST API. Unified data-flow stream processing engines either don't support these scenarios, support them in a limited and constrained way, or are inefficient in supporting them. This is because they are inherently optimized for a **large volume of similar items and are usually limited in terms of expressiveness and processing**. Orleans Streams target these other scenarios.

### Motivation

It all started with requests from Orleans users to support returning a sequence of items from a grain method call. As you can imagine, that was only the tip of the iceberg; they needed much more.

A typical scenario for Orleans Streams is when you have per-user streams and want to perform **different processing for each user** within the context of that individual user. You might have millions of users, but some are interested in weather and subscribe to weather alerts for a particular location, while others are interested in sports events; someone else might be tracking the status of a particular flight. Processing these events requires different logic, but you don't want to run two independent instances of stream processing. Some users might be interested only in a particular stock and only if a certain external condition applies—a condition that might not necessarily be part of the stream data (and thus needs checking dynamically at runtime as part of processing).

Users change their interests all the time, so their subscriptions to specific event streams come and go dynamically. Thus, **the streaming topology changes dynamically and rapidly**. On top of that, **the processing logic per user evolves and changes dynamically based on user state and external events**. External events might modify the processing logic for a particular user. For example, in a game cheating detection system, when a new cheating method is discovered, the processing logic needs updating with the new rule to detect this violation. This must be done, of course, **without disrupting the ongoing processing pipeline**. Bulk data-flow stream processing engines weren't built to support such scenarios.

It goes almost without saying that such a system must run on several network-connected machines, not just a single node. Hence, the processing logic must be distributed scalably and elastically across a cluster of servers.

### New requirements

Four basic requirements were identified for a Stream Processing system to target the scenarios above:

1. Flexible stream processing logic
1. Support for highly dynamic topologies
1. Fine-grained stream granularity
1. Distribution

#### Flexible stream processing logic

The system should support different ways of expressing stream processing logic. Existing systems mentioned above require developers to write a declarative data-flow computation graph, usually following a functional programming style. This limits the expressiveness and flexibility of the processing logic. Orleans streams are indifferent to how processing logic is expressed. It can be expressed as a data flow (e.g., using [Reactive Extensions (Rx) in .NET](/previous-versions/dotnet/reactive-extensions/hh242985(v=vs.103))), a functional program, a declarative query, or general imperative logic. The logic can be stateful or stateless, might or might not have side effects, and can trigger external actions. All power goes to the developer.

#### Support for dynamic topologies

The system should allow for dynamically evolving topologies. Existing systems mentioned above are usually limited to static topologies fixed at deployment time that cannot evolve at runtime. In the following example of a dataflow expression, everything is nice and simple until you need to change it:

``
Stream.GroupBy(x=> x.key).Extract(x=>x.field).Select(x=>x+2).AverageWindow(x, 5sec).Where(x=>x > 0.8) *
``

Change the threshold condition in the <xref:System.Linq.Enumerable.Where*> filter, add a <xref:System.Linq.Enumerable.Select*> statement, or add another branch in the data-flow graph and produce a new output stream. In existing systems, this isn't possible without tearing down the entire topology and restarting the data flow from scratch. Practically, these systems checkpoint the existing computation and can restart from the latest checkpoint. Still, such a restart is disruptive and costly for an online service producing results in real-time. Such a restart becomes especially impractical when dealing with a large number of such expressions executed with similar but different parameters (per-user, per-device, etc.) that continually change.

The system should allow evolving the stream processing graph at runtime by adding new links or nodes to the computation graph or changing the processing logic within the computation nodes.

#### Fine-grained stream granularity

In existing systems, the smallest unit of abstraction is usually the whole flow (topology). However, many target scenarios require an individual node/link in the topology to be a logical entity itself. This way, each entity can potentially be managed independently. For example, in a large stream topology comprising multiple links, different links can have different characteristics and be implemented over different physical transports. Some links might go over TCP sockets, while others use reliable queues. Different links can have different delivery guarantees. Different nodes can have different checkpointing strategies, and their processing logic can be expressed in different models or even different languages. Such flexibility usually isn't possible in existing systems.

The unit of abstraction and flexibility argument is similar to comparing SoA (Service Oriented Architectures) vs. Actors. Actor systems allow more flexibility since each actor is essentially an independently managed "tiny service." Similarly, the stream system should allow for such fine-grained control.

#### Distribution

And of course, the system should have all the properties of a **"good distributed system"**. That includes:

1. _Scalability_: Supports a large number of streams and compute elements.
1. _Elasticity_: Allows adding/removing resources to grow/shrink based on load.
1. _Reliability_: Resilient to failures.
1. _Efficiency_: Uses underlying resources efficiently.
1. _Responsiveness_: Enables near-real-time scenarios.

These were the requirements for building [**Orleans Streaming**](index.md).

_Clarification_: Orleans currently does not directly support writing declarative dataflow expressions like in the example above. The current Orleans Streaming APIs are more low-level building blocks, as described at [Orleans streaming APIs](streams-programming-apis.md).

## See also

[Orleans Streams Programming APIs](streams-programming-apis.md)
