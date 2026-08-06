---
title: Frequently asked questions
description: Explore the frequently asked questions for .NET Orleans.
ms.date: 07/03/2024
---

# Frequently Asked Questions

In this article, you will find answers to the most common questions about .NET Orleans. If you have a question that is not answered here, please ask the product team by posting an issue on the [GitHub repository](https://github.com/dotnet/orleans/issues).

## Availability

### Can I freely use Orleans in my project?

Absolutely. The source code is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE). NuGet packages are published on [nuget.org](https://www.nuget.org/profiles/Orleans).

### Is Orleans production ready? I heard it's a research project.

Orleans started as a research project within Microsoft Research. It later grew into a production-ready product and has been used in production within Microsoft (since 2011) and by other companies (since it was publicly released in 2015). Orleans powers many highly available systems and cloud services.

### Does Microsoft support Orleans?

Microsoft released the source code of Orleans under an MIT license on [GitHub](https://github.com/dotnet/orleans). Microsoft continues to invest in Orleans and accepts community contributions to the codebase.

## Positioning

### Is Orleans a server product? How do I run Orleans?

Orleans is a framework, a set of libraries, that helps you build an application. Orleans-based applications can be run in various hosting environments, in the Cloud or on on-premises clusters, or even on a single machine. It is the responsibility of application developers to build, deploy, and run an Orleans-based application in their target hosting environment.

### Where can I run Orleans?

Orleans can run in any environment where .NET application can run. Before Orleans 2.0, it required the full .NET Framework. Starting with 2.0, Orleans conforms to .NET Standard 2.0, and hence can run on .NET Core in Windows and non-Windows environments that support .NET Core.

### Is Orleans built for Azure?

No. We believe that you should be able to run Orleans anywhere you need, the way you need. Orleans is very flexible and has several optional providers that help host it in cloud environments, such as Azure, AWS or GCP, or on on-premises clusters, with a choice of technologies to support Orleans' clustering protocol.

### What is the difference between Orleans and other actor languages and frameworks, such as Erlang or Akka?

While based on the same base principles of the Actor Model, Orleans took a step forward and introduced a notion of Virtual Actors that greatly simplifies developers' experience and is much more suitable for cloud services and high-scale systems.

## Design

### How big or how small should a grain be in my application?

The grain isolation model makes them very good at representing independently isolated contexts of state and computation. In most cases, grains naturally map to such application entities as users, sessions, accounts. Those entities are generally isolated from each other, can be accessed and updated independently, and expose a well-defined set of supported operations. This works well with the intuitive "one entity, one grain" modeling.

An application entity may be too big to be efficiently represented by a single grain if it encapsulates too much state, and as a result, has to handle a high rate of requests to it. Even though a single grain can generally handle up to a few thousand trivial calls per second, the rule of thumb is to be wary of individual grain receiving hundreds of requests per second. That may be a sign of the grain being too large, and decomposing it into a set of smaller grains may lead to a more stable and balanced system.

An application entity may be too small to be a grain if that would cause constant interaction of other grains with it, and as a result, cause too much of a messaging overhead. In such cases, it may make more sense to make those closely interacting entities part of a single grain, so that they would invoke each other directly.

### How should you avoid grain hot spots?

The throughput of a grain is limited by a single thread that its activation can execute on. Therefore, it is advisable to avoid designs where a single grain receives a disproportionate share of requests or is involved in processing requests to other grains. Various patterns help prevent the overloading of a single grain even when logically it is a central point of communication.

For example, if a grain is an aggregator of some counters or statistics that are reported by a large number of grains regularly, one proven approach is to add a controlled number of intermediate aggregator grains and assign each of the reporting grains (using a modulo on a key or a hash) to an intermediate aggregator, so that the load is more or less evenly distributed across all intermediate aggregator grains that in their turn periodically report partial aggregates to the central aggregator grain.

## How to

### How do I tear down a grain?

In general, there is no need for application logic to force the deactivation of a grain, as the Orleans runtime automatically detects and deactivates idle activations of a grain to reclaim system resources. Letting Orleans do that is more efficient because it batches deactivation operations instead of executing them one by one. In the rare cases when you think you do need to expedite the deactivation of a grain, the grain can do that by calling the <xref:Orleans.Grain.DeactivateOnIdle>` method.

### Can I tell Orleans where to activate a grain?

It is possible to do so using restrictive placement strategies, but we generally consider this a rather advanced pattern that requires careful consideration. By doing what the question suggests, the application would take on the burden of resource management without necessarily having enough information about the global state of the system to do so well. This is especially counter-productive in cases of silo restarts, which in cloud environments may happen regularly for OS patching. Thus, specific placement may hurt your application's scalability as well as resilience to system failure.

That being said, for the rare cases where the application indeed knows where a particular grain should be activated, for example, if it knows the locality of grain's persistent state, in 1.5.0 we introduced custom placement policies and directors.

### How do you version grains or add new grain classes and interfaces?

You can add silos with new grain classes or new versions of existing grain classes to a running cluster.

### Can I Connect to Orleans silos from the public Internet?

Orleans is designed to be hosted as the back-end part of a service, and you are expected to create a front-end tier to which external clients will connect. It can be an HTTP-based Web API project, a socket server, a SignalR server, or anything else that fits the needs of the application. You can connect to Orleans from the Internet if you expose TCP endpoints of silos to it, but it is not a good practice from the security point of view.

### What happens if a silo fails before my grain call returns a response for my call?

In case of a silo failure in the middle of a grain call, you'll receive an exception that you can catch in your code and retry or do something else to handle the error according to your application logic. The grain that failed with the silo will get automatically re-instantiated upon the next call to it. The Orleans runtime does not eagerly recreate grains from a failed silo because many of them may not be needed immediately or at all. Instead, the runtime recreates such grains individually and only when a new request arrives for a particular grain. For each grain it picks one of the available silos as a new host.

The benefit of this approach is that the recovery process is performed only for grains that are being used and it is spread in time and across all available silos, which improves the responsiveness of the system and the speed of recovery. Note also that there is a delay between the time when a silo fails and when the Orleans cluster detects the failure. The delay is a configurable trade-off between the speed of detection and the probability of false positives. During this transition period, all calls to the grain will fail, but after the detection of the failure the grain will be created, upon a new call to it, on another silo, so it will be eventually available.

### What happens if a grain call takes too long to execute?

Since Orleans uses a cooperative multitasking model, it will not preempt the execution of a grain automatically but Orleans generates warnings for long executing grain calls so you can detect them. Cooperative multitasking has a much better throughput compared to preemptive multitasking. Keep in mind that grain calls should not execute any long-running tasks like IO operations synchronously and should not block other tasks to complete. All waiting should be done asynchronously using the `await` keyword or other asynchronous waiting mechanisms. Grains should return as soon as possible to let other grains execute for maximum throughput.
