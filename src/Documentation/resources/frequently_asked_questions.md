---
layout: page
title: Frequently Asked Questions
---
[//]: # (TODO: after files are rearranged and checked for accuracy, put links back)

# Frequently Asked Questions

## Availability

### Can I freely use Orleans in my project?

Absolutely. The source code has been released under the [MIT license](https://github.com/dotnet/orleans/blob/master/LICENSE). NuGet packages are published on [nuget.org](https://www.nuget.org/profiles/Orleans).

### Is Orleans production ready? I heard it's a research project.

Orleans, indeed, initially started as a research project within Microsoft Research.
It later grew into a product and has been used in production within Microsoft since 2011, and by other companies after it was publicly released in 2015.
Orleans is definitely production ready, and powers many highly available systems and cloud services.

### Does Microsoft support Orleans?

Source code of Orleans has been released under an MIT license on [GitHub](https://github.com/dotnet/orleans).
Microsoft continues to invest in Orleans and accepts community contributions to the codebase.

## Positioning

### Is Orleans a server product? How do I run Orleans?

Orleans is a framework, a set of libraries, that helps you build an application.
Orleans-based applications can be run in various hosting environments, in the Cloud or on on-premises clusters or even on a single machine.
It is the responsibility of application developer to build, deploy, and run an Orleans-based application in their target hosting environment.

### Where can I run Orleans?

Orleans can run in any environment where .NET application can run.
Prior to Orleans 2.0, it required full .NET Framework. Starting with 2.0, Orleans conforms to .NET Standard 2.0, and hence can run on .NET Core in Windows and non-Windows environments that support .NET Core.

### Is Orleans built for Azure?

No.
We believe that you should be able to run Orleans anywhere you need, the way you need.
Orleans is very flexible, and has a number of optional providers that help host it in cloud environment, such as Azure, AWS or GCP, or on on-premises clusters, with a choice of technologies to support Orleans' clustering protocol.

### What is the difference between Orleans and other actor languages and frameworks, such as Erlang or Akka?

While based on the same base principles of the Actor Model, Orleans took a step forward, and introduced a notion of Virtual Actors that greatly simplifies developer's experience and is much more suitable for cloud services and high-scale systems.

### Microsoft has another actor model implementation - Azure Service Fabric Reliable Actors. How do I choose between the two?

Reliable Actors are tightly integrated with Service Fabric to leverage its core features, such as replicated in-cluster storage.
Orleans has a richer feature set, is not tied to any particular hosting platform, and can run in almost any environment.
Orleans provides an [optional integration package](https://www.nuget.org/packages/Microsoft.Orleans.Hosting.ServiceFabric/) for hosting Orleans applications in Service Fabric.

In the end, it's the application developer's decision of how much they would benefit from the tight integration of Reliable Actors with the underlying platform of Service Fabric versus the flexibility to run anywhere and the feature set of Orleans.

## Design

### How big or how small should a grain be in my application?

The grain isolation model makes them very good at representing independent isolated contexts of state and computation.
In most cases, grains naturally map to such application entities as users, sessions, accounts.
Those entities are generally isolated from each other, can be accessed and updated independently, and expose a well defined set of supported operations.
This works well with the intuitive "one entity - one grain" modeling.

An application entity may be too big to be efficiently represented by a single grain if it encapsulates too much state, and as a result has to handle a high rate of requests to it.
Even though a single grain can generally handle up to a few thousand trivial calls per second, the rule of thumb is to be wary of individual grain receiving hundreds of requests per second.
That may be a sign of the grain being too large, and decomposing it into a set of smaller grains may lead to a more stable and balanced system.

An application entity may be too small to be a grain if that would cause constant interaction of other grains with it, and as a result, cause too much of a messaging overhead.
In such cases, it may make more sense to make those closely interacting entities part of a single grain, so that they would invoke each other directly.

### How should you avoid grain hot spots?

The throughput of a grain is limited by a single thread that its activation can execute on.
Therefore, it is advisable to avoid designs where a single grain receives a disproportionate share of requests or is involved in processing requests to other grains.
There are various patterns that help prevent overloading of a single grain even when logically it is a central point of communication.

For example, if a grain is an aggregator of some counters or statistics that are reported by a large number of grains on a regular basis, one proven approach is to add a controlled number of intermediate aggregator grains and assign each of the reporting grains (using a modulo on a key or a hash) to an intermediate aggregator, so that the load is more or less evenly distributed across all intermediate aggregator grains that in their turn periodically report partial aggregates to the central aggregator grain.

### Can a single Orleans cluster run across multiple datacenters?

Orleans clusters are currently limited to a single data center per cluster.
Instead, since 1.3.0, you can consider a multi-cluster deployment where clusters deployed to different datacenters form a single multi-cluster. 

### In what cases can a split brain (same grain activated in multiple silos at the same time) happen?

During normal operations the Orleans runtime guarantees that each grain will have at most one instance in the cluster.
The only time this guarantee can be violated is when a silo crashes or gets killed without a proper shutdown.
In that case, there is a ~30 second (based on configuration) window where a grain can potentially get temporarily instantiated in more than one silo.
Convergence to a single instance per grain is guaranteed, and duplicate activations will be deactivated when this window closes.

Also you can take a look at Orleans' [paper](http://research.microsoft.com/pubs/210931/Orleans-MSR-TR-2014-41.pdf) for a more detailed information, however you don't need to understand it fully to be able to write your application code.
You just need to consider the rare possibility of having two instances of an actor while writing your application.
The persistence model guarantees that no writes to storage are blindly overwritten in such a case.

## How To

### How do I tear down a grain?

In general there is no need for application logic to force deactivation of a grain, as the Orleans runtime automatically detects and deactivates idle activations of a grain to reclaim system resources.
Letting Orleans do that is more efficient because it batches deactivation operations instead of executing them one by one.
In the rare cases when you think you do need to expedite deactivation of a grain, the grain can do that by calling the `base.DeactivateOnIdle()` method.

### Can I tell Orleans where to activate a grain?

It is possible to do so using restrictive placement strategies, but we generally consider this a rather advanced pattern that requires careful consideration.
By doing what the question suggests, the application would take on the burden of resource management without necessarily having enough information about the global state of the system to do so well.
This is especially counter-productive in cases of silo restarts, which in cloud environments may happen on a regular basis for OS patching.
Thus, specific placement may have a negative impact on your application's scalability as well as resilience to system failure.

That being said, for the rare cases where the application indeed knows where a particular grain should be activated, for example, if it has a knowledge of the locality of grain's persistent state, in 1.5.0 we introduced custom placement policies and directors.

### How do you version grains or add new grain classes and interfaces?

You can add silos with new grain classes or new versions of existing grain classes to a running cluster.

### Can I Connect to Orleans silos from the public Internet?

Orleans is designed to be hosted as the back-end part of a service, and you are expected to create a front-end tier to which external clients will connect.
It can be an HTTP based Web API project, a socket server, a SignalR server or anything else fits the needs of the application.
You can connect to Orleans from the Internet if you expose TCP endpoints of silos to it, but it is not a good practice from the security point of view.

### What happens if a silo fails before my grain call returns a response for my call?

In case of a silo failure in the middle of a grain call, you'll receive an exception that you can catch in your code and retry or do something else to handle the error according to your application logic.
The grain that failed with the silo will get automatically re-instantiated upon a next call to it.
The Orleans runtime does not eagerly recreate grains from a failed silo because many of them may not be needed immediately or at all.
Instead, the runtime recreates such grains individually and only when a new request arrives for a particular grain.
For each grain it picks one of the available silos as a new host.

The benefit of this approach is that the recovery process is performed only for grains that are actually being used and it is spread in time and across all available silos, which improves the responsiveness of the system and the speed of recovery.
Note also that there is a delay between the time when a silo fails and when the Orleans cluster detects the failure.
The delay is a configurable tradeoff between the speed of detection and the probability of false positives.
During this transition period all calls to the grain will fail, but after the detection of the failure the grain will be created, upon a new call to it, on another silo, so it will be eventually available.

### What happens if a grain call takes too long to execute?

Since Orleans uses a cooperative multi-tasking model, it will not preempt the execution of a grain automatically but Orleans generates warnings for long executing grain calls so you can detect them.
Cooperative multi-tasking has a much better throughput compared to preemptive multi-tasking.
Keep in mind that grain calls should not execute any long running tasks like IO operations synchronously and should not block on other tasks to complete.
All waiting should be done asynchronously using the `await` keyword or other asynchronous waiting mechanisms.
Grains should return as soon as possible to let other grains execute for maximum throughput.
