---
layout: page
title: Why Orleans Streams?
---
{% include JB/setup %}


There is already a wide range of technologies that allow to build stream processing systems.
Those include systems to **durably store events** (examples are [Event Hubs](http://azure.microsoft.com/en-us/services/event-hubs/) and [Kafka](http://kafka.apache.org/)) and systems to express **compute operations** over the stream data (examples include [Azure Stream Analytics](http://azure.microsoft.com/en-us/services/stream-analytics/), [Apache Storm](https://storm.apache.org/), and [Apache Spark Streaming](https://spark.apache.org/streaming/)). Those are great systems that allow to build  stream processing pipelines.

### Limitations of Existing Systems

However, those systems are not suitable for **fine-grained compute over stream data**. The Streaming Compute systems mentioned above all allow to specify a **unified data flow graph of operations that are applied in the same way to all stream items**. This is a powerful model, when data is uniform and you want to express the same set of transformations, filtering or aggregation operations over this data.
But what if you need to express fundamentally different operations over different data items? And what if as part of this processing you occasionally need to make an external call, such as invoke some arbitrary REST API? And what if the processing, in terms of cost, is very different between different items? The unified data flow stream processing engines either do not support those scenarios, support them in a very limited and constrained way, or are very inefficient in supporting those. This is since they are inherently optimized for **large volume of similar items with similar, and usually limited in terms of expressiveness, processing**.

### Motivation - Dynamic and Flexiable Processing Logic

Orleans Streams target those other scenarios. Imagine a situation when you have a per user stream and you want to perform **different processing for each user**, depending on the particular application or scenario in which this user is currently interested. Some users are interested in weather and can subscribe to weather alerts, while some in sport events. Processing those events requires different logic, but you don't wnat to run two indepenedant instances of stream processing.
Some users are interested in only a particular stock and only if certain external condition applies, condition that may not necessarily be part of the stream data (thus needs to be checked dynamically at runtime as part of processing). Also imagine that those user come and go dynamically, thus **the streaming topology changes dynamically and rapidly**. And now imagine that **the processing logic per user evolves and changes dynamically as well, based on some external events**. Those external events need an ability to notify and modify the per-user processing logic. For example, in a game cheating detection system, when a new way to cheat is discovered the processing logic needs to be updates to detect this new violation. This needs to be done of course **without disrupting the ongoing processing pipeline**. Bulk data flow stream processing engines were not build to support those scenarios.

### New Requirements

We identified 4 basic requirements for the our new Stream Processing system that will allow it to target the above scenarios.

1. Flexibale stream processing logic
2. Support for dynamc topologies
3. Fine grained stream granularity
4. Distribution

We detail each of those below.

**Flexibale stream processing logic**

Our system should allow multiple ways to express stream processing logic. The existing systems we mentioned above limit the developer to write a declarative data-flow computation graph, usually by following a functional programming paradygm. This limits the expressiveness of the processing logic. Orleans streams are indeferent to the way processing logic is expressed. It can be expressied as a data-flow (e.g., by using [Reactive Extensions (Rx) in .NET](https://msdn.microsoft.com/en-us/data/gg577609.aspx), as functional program, as declarative query, or as a general imperative logic. The logic can be statefull or stateless, may have side effects and can trigger external actions.

**Support for dynamc topologies**

Our system shoudl allow dynamcic evolving topologies. The existing systems we mentioned above are usually limited to only static topologoes, that are expressed at compile or deploy time and cannot evolve at runtime. For example, imagine a following data-flow graph expressed in one of the above systems:

``
Stream.GroupBy(x=> x.key).Extract(x=>x.field).Select(x=>x+2).AverageWindow(x, 5sec).Where(x=>x > 0.8) 
``

and now imagine that you want to change the threshold condition in the Where filter. Or even add a new Select statement. In existing system tis is not possible without tearing down the entire toplogy and restaring the data-flow from scratch. Practicaly, those system will checkpoint he existing computation and will be able to restart from the latet checkpoint. Still, such a restart is desruptive and costly to online service, that produces resulst in real time.






