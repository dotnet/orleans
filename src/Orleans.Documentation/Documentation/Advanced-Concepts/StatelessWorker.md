---
layout: page
title: Stateless Worker Grains
---

# Stateless Worker Grains

By default, the Orleans runtime creates no more than one activation of a grain within the cluster.
This is the most intuitive expression of the Virtual Actor model with each grain corresponding to an entity with a unique type/identity.
However, there are also cases when an application needs to perform functional stateless operations that are not tied to a particular entity in the system.
For example, if client sends requests with compressed payloads that need to be decompressed before they could be routed to the target grain for processing, such decompression/routing logic is not tied to a specific entity in the application, and can easily scale out.

When the `[StatelessWorker]` attribute is applied to a grain class, it indicates to the Orleans runtime that grains of that class should be treated as **Stateless Worker** grains.
**Stateless Worker** grains have the following properties that make their execution very different from that of normal grain classes.

1. The Orleans runtime can and will create multiple activations of a Stateless Worker grain on different silos of the cluster.
2. Requests made to Stateless Worker grains are always executed locally, that is on the same silo where the request originated, either made by a grain running on the silo or received by the silo's client gateway.
Hence, calls to Stateless Worker grains from other grains or from client gateways never incur a remote message.
3. The Orleans Runtime automatically creates additional activations of a Stateless Worker grain if the already existing ones are busy.
The maximum number of activations of a Stateless Worker grain the runtime creates per silo is limited by default by the number of CPU cores on the machine, unless specified explicitly by the optional `maxLocalWorkers` argument.
4. Because of 2 and 3, Stateless Worker grain activations are not individually addressable. Two subsequent requests to a Stateless Worker grain may be processed by different activations of it.

Stateless Worker grains provide a straightforward way of creating an auto-managed pool of grain activations that automatically scales up and down based on the actual load.
The runtime always scans for available Stateless Worker grain activations in the same order.
Because of that, it always dispatches a requests to the first idle local activation it can find, and only gets to the last one if all previous activations are busy.
If all activations are busy and the activation limit hasn't been reached, it creates one more activation at the end of the list, and dispatches the request to it.
That means that when the rate of requests to a Stateless Worker grain increases, and existing activations are all currently busy, the runtime expands the pool of its activations up to the limit.
Conversely, when the load drops, and it can be handled by a smaller number of activations of the Stateless Worker grain, the activations at the tail of the list will not be getting requests dispatched to them.
They will become idle, and eventually deactivated by the standard activation collection process.
Hence, the pool of activations will eventually shrink to match the load.

The following example defines a Stateless Worker grain class `MyStatelessWorkerGrain` with the default maximum activation number limit. 
``` csharp
[StatelessWorker]
public class MyStatelessWorkerGrain : Grain, IMyStatelessWorkerGrain
{
 ...
}
```

Making a call to a Stateless Worker grain is the same as to any other grain.
The only difference is that in most cases a single grain ID is used, 0 or `Guid.Empty`.
Multiple grain IDs can be used when having multiple Stateless Worker grain pools, one per ID, is desirable.

``` csharp
var worker = GrainFactory.GetGrain<IMyStatelessWorkerGrain>(0);
await worker.Process(args);
```


This one defines a Stateless Worker grain class with no more than one grain activation per silo. 
``` csharp
[StatelessWorker(1)] // max 1 activation per silo
public class MyLonelyWorkerGrain : ILonelyWorkerGrain
{
 ...
}
```

Note that `[StatelessWorker]` attribute does not change reentrancy of the target grain class.
Just like any other grains, Stateless Worker grains are non-reentrant by default.
They can be explicitly made reentrant by adding a `[Reentrant]` attribute to the grain class.

## State

The "Stateless" part of "Stateless Worker" does not mean that a Stateless Worker cannot have state and is limited only to executing functional operations.
Like any other grain, a Stateless Worker grain can load and keep in memory any state it needs.
It's just because multiple activations of a Stateless Worker grain can be created on the same and different silos of the cluster, there is no easy mechanism to coordinate state held by different activations.

There are several useful patterns that involve Stateless Worker holding state.

### Scaled out hot cache items

For hot cache items that experience high throughput, holding each such item in a Stateless Worker grain makes it 
a) automatically scale out within a silo and across all silos in the cluster; 
and b) makes the data always locally available on the silo that received the client request via its client gateway, so that the requests can be answered without an extra network hop to another silo.


### Reduce style aggregation

In some scenarios applications need to calculate certain metrics across all grains of a particular type in the cluster, and report the aggregates periodically.
Examples are reporting number of players per game map, average duration of a VoIP call, etc.
If each of the many thousands or millions of grains were to report their metrics to a single global aggregator, the aggregator would get immediately overloaded unable to process the flood of reports.
The alternative approach is to turn this task into a 2 (or more) step reduce style aggregation.
The first layer of aggregation is done by reporting grain sending their metrics to a Stateless Worker pre-aggregation grain.
The Orleans runtime will automatically create multiple activations of the Stateless Worker grain with each silo.
Since all such calls will be processed locally with no remote calls or serialization of the messages, the cost of such aggregation will be significantly less than in a remote case.
Now each of the pre-aggregation Stateless Worker grain activations, independently or in coordination with other local activations,
can send their aggregated reports to the global final aggregator (or to another reduction layer if necessary) without overloading it.

