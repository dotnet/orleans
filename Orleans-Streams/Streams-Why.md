---
layout: page
title: Orleans Streams Why?
---
{% include JB/setup %}


There is already a wide range of technologies that allow to build stream processing systems.
Those include systems to durably store events (examples are [Event Hubs](http://azure.microsoft.com/en-us/services/event-hubs/) and [Kafka](http://kafka.apache.org/)) and systems to express compute operations over the stream data (examples include [Azure Stream Analytics](http://azure.microsoft.com/en-us/services/stream-analytics/), [Apache Storm](https://storm.apache.org/), and [Apache Spark Streaming](https://spark.apache.org/streaming/)). Those are great systems that allow to build  stream processing pipelines.

However, those systems are not suitable for **fine-grained compute over stream data**. [Azure Stream Analytics](http://azure.microsoft.com/en-us/services/stream-analytics/), [Apache Storm](https://storm.apache.org/), and [Apache Spark Streaming](https://spark.apache.org/streaming/)) all allow to specify a unified data flow graph of operations that are applied in the same way to all stream items. This is a powerful model, when data is uniform and you want to express the same set of transformations, filtering or aggregation operations over this data.
But what if you need to express fundamentally different operations over different data items? And what if as part of this processing you occasionally need to make an external call, such as invoke some arbitrary REST API? And what if the processing, in terms of cost is very different between different items? The unified data flow stream processing engines either do not support those scenarios or are very inefficient in them. This is since they are inherently optimized for large volume of similar items with similar, and usually limited in terms of expressiveness, processing.

Orleans Streams targets those other scenarios. Imagine a situation when you have a per user stream and you want to perform different processing for each user, depending on the particular application or scenario in which this user is currently interested. Some user are interested in weather and can subscribe to weather alerts, while some in sport events, some are interested in only a particular stock and only if certain external condition applies, condition that may not necessarily be part of the stream data (thus needs to be checked dynamically at runtime as part of processing). Also imagine those user come and go dynamically. And now imagine the  processing logic per user evolve and change dynamically as well, based on some external events. Bulk data flow stream processing engines do not support those scenarios.
