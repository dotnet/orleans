---
title: Stateless worker grains
description: Learn how to use stateless worker grains in .NET Orleans.
ms.date: 05/23/2025
ms.topic: concept-article
---

# Stateless worker grains

By default, the Orleans runtime creates no more than one activation of a grain within the cluster. This is the most intuitive expression of the Virtual Actor model, where each grain corresponds to an entity with a unique type/identity. However, sometimes an application needs to perform functional stateless operations not tied to a particular entity in the system. For example, if a client sends requests with compressed payloads that need decompression before routing to the target grain for processing, such decompression/routing logic isn't tied to a specific entity and can easily scale out.

When you apply the <xref:Orleans.Concurrency.StatelessWorkerAttribute> to a grain class, it indicates to the Orleans runtime that grains of that class should be treated as stateless worker grains. Stateless worker grains have the following properties that make their execution very different from normal grain classes:

1. The Orleans runtime can and does create multiple activations of a stateless worker grain on different silos in the cluster.
1. Stateless worker grains execute requests locally as long as the silo is compatible, therefore incurring no networking or serialization costs. If the local silo isn't compatible, requests are forwarded to a compatible silo.
1. The Orleans Runtime automatically creates additional activations of a stateless worker grain if the existing ones are busy. The maximum number of activations per silo is limited by default by the number of CPU cores on the machine, unless you specify it explicitly using the optional `maxLocalWorkers` argument.
1. Because of points 2 and 3, stateless worker grain activations aren't individually addressable. Two subsequent requests to a stateless worker grain might be processed by different activations.

Stateless worker grains provide a straightforward way to create an auto-managed pool of grain activations that automatically scales up and down based on the actual load. The runtime always scans for available stateless worker grain activations in the same order. Because of this, it always dispatches requests to the first idle local activation it finds and only proceeds to the last one if all previous activations are busy. If all activations are busy and the activation limit hasn't been reached, it creates one more activation at the end of the list and dispatches the request to it. This means that when the rate of requests to a stateless worker grain increases and existing activations are all busy, the runtime expands the pool of activations up to the limit. Conversely, when the load drops and a smaller number of activations can handle it, the activations at the tail of the list won't receive requests. They become idle and are eventually deactivated by the standard activation collection process. Thus, the pool of activations eventually shrinks to match the load.

The following example defines a stateless worker grain class `MyStatelessWorkerGrain` with the default maximum activation number limit.

```csharp
[StatelessWorker]
public class MyStatelessWorkerGrain : Grain, IMyStatelessWorkerGrain
{
    // ...
}
```

Making a call to a stateless worker grain is the same as calling any other grain. The only difference is that in most cases, you use a single grain ID, for example, `0` or <xref:System.Guid.Empty?displayProperty=nameWithType>. You can use multiple grain IDs if having multiple stateless worker grain pools (one per ID) is desirable.

```csharp
var worker = GrainFactory.GetGrain<IMyStatelessWorkerGrain>(0);
await worker.Process(args);
```

This example defines a stateless worker grain class with no more than one activation per silo.

```csharp
[StatelessWorker(1)] // max 1 activation per silo
public class MyLonelyWorkerGrain : ILonelyWorkerGrain
{
    //...
}
```

Note that <xref:Orleans.Concurrency.StatelessWorkerAttribute> doesn't change the reentrancy of the target grain class. Like any other grain, stateless worker grains are non-reentrant by default. You can explicitly make them reentrant by adding a <xref:Orleans.Concurrency.ReentrantAttribute> to the grain class.

## State

The "stateless" part of "stateless worker" doesn't mean a stateless worker cannot have state or is limited only to executing functional operations. Like any other grain, a stateless worker grain can load and keep in memory any state it needs. However, because multiple activations of a stateless worker grain can be created on the same and different silos in the cluster, there's no easy mechanism to coordinate state held by different activations.

Several useful patterns involve stateless workers holding state.

### Scaled-out hot cache items

For hot cache items experiencing high throughput, holding each item in a stateless worker grain provides these benefits:

1. It automatically scales out within a silo and across all silos in the cluster.
1. It makes the data always locally available on the silo that received the client request via its client gateway, allowing requests to be answered without an extra network hop to another silo.

### Reduce-style aggregation

In some scenarios, applications need to calculate certain metrics across all grains of a particular type in the cluster and report the aggregates periodically. Examples include reporting the number of players per game map or the average duration of a VoIP call. If each of the many thousands or millions of grains reported their metrics to a single global aggregator, the aggregator would immediately become overloaded and unable to process the flood of reports. The alternative approach is to turn this task into a two-step (or more) reduce-style aggregation. The first layer of aggregation involves the reporting grains sending their metrics to a stateless worker pre-aggregation grain. The Orleans runtime automatically creates multiple activations of the stateless worker grain on each silo. Since Orleans processes all such calls locally without remote calls or message serialization, the cost of this aggregation is significantly less than in a remote case. Now, each pre-aggregation stateless worker grain activation, independently or in coordination with other local activations, can send its aggregated report to the global final aggregator (or to another reduction layer if necessary) without overloading it.
