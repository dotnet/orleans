---
layout: page
title: Why Orleans Streams?
---

# Why Orleans Streams?

There are already a wide range of technologies that allow you to build stream processing systems.
Those include systems to **durably store stream data** (e.g., [Event Hubs](http://azure.microsoft.com/en-us/services/event-hubs/) and [Kafka](http://kafka.apache.org/)) and systems to express **compute operations** over stream data (e.g., [Azure Stream Analytics](http://azure.microsoft.com/en-us/services/stream-analytics/), [Apache Storm](https://storm.apache.org/), and [Apache Spark Streaming](https://spark.apache.org/streaming/)). Those are great systems that allow you to build efficient data stream processing pipelines.

### Limitations of Existing Systems
However, those systems are not suitable for **fine-grained free-form compute over stream data**. The Streaming Compute systems mentioned above all allow you to specify a **unified data-flow graph of operations that are applied in the same way to all stream items**. This is a powerful model when data is uniform and you want to express the same set of transformation, filtering, or aggregation operations over this data.
But there are other use cases where you need to express fundamentally different operations over different data items. And in some of them as part of this processing you occasionally need to make an external call, such as invoke some arbitrary REST API. The unified data-flow stream processing engines either do not support those scenarios, support them in a limited and constrained way, or are inefficient in supporting them. This is because they are inherently optimized for a **large volume of similar items, and usually limited in terms of expressiveness, processing**. Orleans Streams target those other scenarios.

### Motivation
It all started with requests from Orleans users to support returning a sequence of items from a grain method call. As you can imagine, that was only the tip of the iceberg. They actually needed much more than that.

A typical scenario for Orleans Streams is when you have per user streams and you want to perform **different processing for each user**, within the context of an individual user. We may have millions of users but some of them are interested in weather and can subscribe to weather alerts for a particular location, while some are interested in sports events; somebody is tracking status of a particular flight. Processing those events requires different logic, but you don't want to run two independent instances of stream processing. Some users are interested in only a particular stock and only if certain external condition applies, condition that may not necessarily be part of the stream data (thus needs to be checked dynamically at runtime as part of processing).

Users change their interests all the time, hence their subscriptions to specific streams of events come and go dynamically, thus **the streaming topology changes dynamically and rapidly**. On top of that, **the processing logic per user evolves and changes dynamically as well, based on user state and external events**. External events may modify the  processing logic for a particular user. For example, in a game cheating detection system, when a new way to cheat is discovered the processing logic needs to be updated with the new rule to detect this new violation. This needs to be done of course **without disrupting the ongoing processing pipeline**. Bulk data-flow stream processing engines were not build to support such scenarios.

It goes almost without saying that such a system has to run on a number of network-connected machines, not on a single node. Hence, the processing logic has to be distributed in a scalable and elastic manner across a cluster of servers.

### New Requirements

We identified 4 basic requirements for our Stream Processing system that will allow it to target the above scenarios.

1. Flexible stream processing logic
2. Support for highly dynamic topologies
3. Fine-grained stream granularity
4. Distribution

#### Flexible stream processing logic

We want the system to support different ways of expressing the stream processing logic. The existing systems we mentioned above require the developer to write a declarative data-flow computation graph, usually by following a functional programming style. This limits the expressiveness and flexibility of the processing logic. Orleans streams are indifferent to the way processing logic is expressed. It can be expressed as a data-flow (e.g., by using [Reactive Extensions (Rx) in .NET](https://msdn.microsoft.com/en-us/data/gg577609.aspx)); as a functional program; as a declarative query; or in a general imperative logic. The logic can be stateful or stateless, may or may not have side effects, and can trigger external actions. All power goes to the developer.

#### Support for dynamic topologies

We want the system to allow for dynamically evolving topologies. The existing systems we mentioned above are usually limited to only static topologies that are fixed at deployment time and cannot evolve at runtime. In the following example of a dataflow expression everything is nice and simple until you need to change it.

``
Stream.GroupBy(x=> x.key).Extract(x=>x.field).Select(x=>x+2).AverageWindow(x, 5sec).Where(x=>x > 0.8) *
``

Change the threshold condition in the `Where` filter, add an additional `Select` statement or add another branch in the data-flow graph and produce a new output stream. In existing systems this is not possible without tearing down the entire topology and restarting the data-flow from scratch. Practically, those systems will checkpoint the existing computation and will be able to restart from the latest checkpoint. Still, such a restart is disruptive and costly to an online service that produces results in real time. Such a restart becomes especially impractical when we are talking about a large number of such expressions being executed with similar but different (per-user, per-deveice, et.) parameters and that keep constantly changing.

We want the system to allow for evolving the stream processing graph at runtime, by adding new links or nodes to the computation graph, or by changing the processing logic within the computation nodes.

#### Fine grained stream granularity

In the existing systems, the smallest unit of abstraction is usually the whole flow (topology). However, many of our target scenarios require individual node/link in the topology to be a logical entity by itself. That way each entity can be potentially managed independently. For example, in the big stream topology comprising of multiple links, different links can have different characteristics and can be implemented over different physical transports. Some links can go over TCP sockets, while others over reliable queues. Different links can have different delivery guarantees. Different nodes can have different checkpointing strategies, and their processing logic can be expressed in different models or even different languages. Such flexibility is usually not possible in existing systems.

The unit of abstraction and flexibility argument is similar to comparison of SoA (Service Oriented Architectures) vs. Actors. Actor systems allow more flexibility, since each is essentially an independently managed ''tiny service''. Similarly, we want the system to allow for such a fine grained control.

#### Distribution

And of course, our system should have all the properties of a **"good distributed system"**. That includes:

1. _Scalability_ - supports large number of streams and compute elements.
2. _Elasticity_ - allows to add/remove resources to grow/shrink based on load.
3. _Reliability_ - be resilient to failures
4. _Efficiency_ - use the underlying resources efficiently
5. _Responsiveness_ - enable near real time scenarios.

These were the requirements we had in mind for building [**Orleans Streaming**](index.md).

---

_Clarificaton_: Orleans currently does not directly support writing declarative dataflow expressions like in the example above. The current Orleans Streaming APIs are more low level building blocks, as described [here](Streams-Programming-APIs.md). Providing declarative dataflow expressions is our future goal.

## Next
[Orleans Streams Programming APIs](Streams-Programming-APIs.md)
