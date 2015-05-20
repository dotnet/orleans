---
layout: page
title: Why Orleans Streams?
---
{% include JB/setup %}


There is already a wide range of technologies that allow to build stream processing systems.
Those include systems to **durably store stream data** (e.g., [Event Hubs](http://azure.microsoft.com/en-us/services/event-hubs/) and [Kafka](http://kafka.apache.org/)) and systems to express **compute operations** over the stream data (e.g., [Azure Stream Analytics](http://azure.microsoft.com/en-us/services/stream-analytics/), [Apache Storm](https://storm.apache.org/), and [Apache Spark Streaming](https://spark.apache.org/streaming/)). Those are great systems that allow to build effecient data stream processing pipelines.

### Limitations of Existing Systems
However, those systems are not suitable for **fine-grained heterogeneous compute over stream data**. The Streaming Compute systems mentioned above all allow to specify a **unified data-flow graph of operations that are applied in the same way to all stream items**. This is a powerful model, when data is uniform and you want to express the same set of transformations, filtering or aggregation operations over this data.
But what if you need to express fundamentally different operations over different data items? And what if as part of this processing you occasionally need to make an external call, such as invoke some arbitrary REST API? And what if the processing, in terms of cost, is very different between different items? The unified data-flow stream processing engines either do not support those scenarios, support them in a very limited and constrained way, or are very inefficient in supporting those. This is since they are inherently optimized for **large volume of similar items with similar, and usually limited in terms of expressiveness, processing**.

### Motivation - Dynamic and Flexible Processing Logic

Orleans Streams target those other scenarios. Imagine a situation when you have a per user stream and you want to perform **different processing for each user**, depending on the particular application or scenario in which this user is currently interested. Some users are interested in weather and can subscribe to weather alerts, while some in sport events. Processing those events requires different logic, but you don't want to run two independent instances of stream processing.
Some users are interested in only a particular stock and only if certain external condition applies, condition that may not necessarily be part of the stream data (thus needs to be checked dynamically at runtime as part of processing). Also imagine that those user come and go dynamically, thus **the streaming topology changes dynamically and rapidly**. And now imagine that **the processing logic per user evolves and changes dynamically as well, based on some external events**. Those external events need an ability to notify and modify the per-user processing logic. For example, in a game cheating detection system, when a new way to cheat is discovered the processing logic needs to be updated with the new rule to detect this new violation. This needs to be done of course **without disrupting the ongoing processing pipeline**. Bulk data-flow stream processing engines were not build to support those scenarios.

### New Requirements

We identified 4 basic requirements for our Stream Processing system that will allow it to target the above scenarios.

1. Flexible stream processing logic
2. Support for dynamic topologies
3. Fine grained stream granularity
4. Distribution

**Flexible stream processing logic**

Our system should allow multiple ways to express the stream processing logic. The existing systems we mentioned above limit the developer to write a declarative data-flow computation graph, usually by following a functional programming style. This limits the expressiveness of the processing logic. Orleans streams are indifferent to the way processing logic is expressed. It can be expressed as a data-flow (e.g., by using [Reactive Extensions (Rx) in .NET](https://msdn.microsoft.com/en-us/data/gg577609.aspx)); as a functional program; as a declarative query; or in a general imperative logic. The logic can be statefull or stateless, may have side effects and can trigger external actions.

**Support for dynamic topologies**

Our system should allow dynamic evolving topologies. The existing systems we mentioned above are usually limited to only static topologies that are fixed at deployment time and cannot evolve at runtime. For example, imagine a following data-flow graph expressed in one of the above systems:

``
Stream.GroupBy(x=> x.key).Extract(x=>x.field).Select(x=>x+2).AverageWindow(x, 5sec).Where(x=>x > 0.8) 
``

and now imagine that you want to change the threshold condition in the `Where` filter. Or even add a new `Select` statement. Or add another branch in the data-flow graph and produce a new output stream.
In existing systems this is not possible without tearing down the entire topology and restarting the data-flow from scratch. Practically, those systems will checkpoint the existing computation and will be able to restart from the latest checkpoint. Still, such a restart is disruptive and costly to an online service that produces results in real time.

Our system should be able to evolve the stream processing graph at runtime, by adding new links or nodes to the computation graph, or by changing the processing logic within the computation nodes.

**Fine grained stream granularity**

In the existing systems the smallest unit of abstraction is usually the whole flow (topology). However, a lot of our target scenarios require individual node/link in the topology to be a logical entity by itself. That way each entity can be potentially managed independently. For example, in the big stream topology comprising of multiple links, different links can have different characteristics and can be implemented over different physical transports. Some links can go over TCP sockets, while others over reliable queues. Different links can have different delivery guarantees. Different nodes can have different checkpointing strategies, and their processing logic can be expressed in different models or even different languages. Such flexibility is usually not possible in existing systems.

The unit of abstraction and flexibility argument is similar to comparison of SoA (Service Oriented Architectures) vs. Actors. Actor systems allow more flexibility, since each is essentially an independently managed ``tiny service''. Therefore, our system should allow such fine grained control.

**Distribution**

And of course, our system should have all the properties of a **``good distributed system''**. That includes:

1. _Scalability_ - supports large number of streams and compute elements.
2. _Elasticity_ - allows to add/remove resources to grow/shrink based on load.
3. _Reliability_ - be resilient to failures
4. _Efficiency_ - use the underlying resources efficiently
5. _Responsiveness_ - enable near real time scenarios.

With those requirements in mind we set to build [**Orleans Streaming**](index).
