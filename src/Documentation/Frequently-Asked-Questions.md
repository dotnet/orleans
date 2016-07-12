---
layout: page
title: Frequently Asked Questions
---

# Frequently Asked Questions

## Does Microsoft support Orleans?

Source code of Orleans has been released under an MIT license on [GitHub](https://github.com/dotnet/orleans). Microsoft continues to invest in Orleans and accepts community contributions to the codebase.

## Can I get a "Go Live" License?

The source code has been releases under the [MIT license](https://github.com/dotnet/orleans/blob/master/LICENSE).

## When will Orleans be production ready?

Orleans has been production ready and used in production for several years.

## When should I use a grain and when should I use a plain old object?

There are two ways to answer this, from a runtime behavior perspective, and from a modeling perspective.

The **runtime behavior perspective** is that an object can only be created within a grain and is not remotely accessible. Grains are accessible from anywhere in the system and are location-transparent, so that they can be automatically placed on any server in the deployment, and they survive failures or reboots of servers.

The **modeling perspective**: there are four kinds of "things" in your Orleans-based application: communication interfaces, message payloads, grains, and data held privately by grains. Objects should be used for payloads and data held by grains; communication interfaces are regular interfaces with some minor restrictions. The question that remains, then, is what entities in a given system should be modeled as grains?

Generally, you should use a grain to model an independent entity which has a publicly exposed communication interface with other components of the system and that has a life of its own – that is, it can exist independently from other components. For example, a user in social network is a grain, while it's name is not. A user’s news wall may be a grain, while the list of the messages it received is not (since the wall is accessible by other users, while the list of messages is a private data to that user only). Hopefully, the samples on this site will help you identify some of the patterns and see parallels to your own scenarios.

## How should you avoid grain hot spots?

The throughput of a grain is limited by a single thread that its activation can execute on. Therefore, it is advisable to avoid designs where a single grain receives a disproportionate share of requests. There are various patterns that help prevent overloading of a single grain even when logically it is a central point of communication.

For example, if a grain is an aggregator of some counters or statistics that are reported by a large number of grains on a regular basis, one proven approach is to add a controlled number of intermediate aggregator grains and assign each of the reporting grains (using a modulo on a key or a hash) to an intermediate aggregator, so that the load is more or less evenly distributed across all intermediate aggregator grains that in their turn periodically report partial aggregates to the central aggregator grain.

## How do I tear down a grain?

In general there is no need for application logic to force deactivation of a grain, as the Orleans runtime automatically detects and deactivates idle activations of a grain to reclaim system resources. In rare cases when you think you do need to expedite deactivation of a grain, the grain can do that by calling the `base.DeactivateOnIdle()` method.

## Can I tell Orleans where to activate a grain?

It is possible to do so using restrictive placement strategies, but we generally consider this an anti-pattern, so it is not recommended. If you find yourself needing to specify a specific silo for grain activation, you are likely not modeling your system to take full advantage of Orleans.

By doing what the question suggests, the application would take on the burden of resource management without necessarily having enough information about the global state of the system to do so well. This is especially counter-productive in cases of silo restarts, which in cloud environments may happen on a regular basis for OS patching. Thus, specific placement may have a negative impact on your application's scalability as well as resilience to system failure.

## Can a single deployment run across multiple data centers?

Orleans deployments are currently limited to a single data center per deployment.

## Can I hot deploy grains either adding or updating them?

No, not currently.

## How do you version grains?

Orleans currently does not support updating grain code on the fly. To upgrade to a new version of grain code, you need to provision and start a new deployment, switch incoming traffic to it, and then shut down the old deployment.

## Can I persist a grain’s state to the Azure cache service?

This can be done though a storage provider for Azure Cache. We don’t have one but you can easily build your own.

## Can I Connect to Orleans silos from the public internet?

Orleans is designed to be hosted as the back-end part of a service and you are suposed to create a front-end in your servers which externa clients connect to. It can be an http based Web API project, a socket server, a SignalR server or anything else which you require. You can actually connect to Orleans from the internet, but it is not a good practice from the security point of view.

## What happens if a silo fails before my grain call returns a response for my call?

You'll receive an exception which you can catch and retry or do anything else which makes sense in your application logic. The Orleans runtime does not immediately recreate grains from a failed silo because many of them may not be needed immediately or at all. Instead, the runtime recreates such grains individually and only when a new request arrives for a particular grain. For each grain it picks one of the available silos as a new host. The benefit of this approach is that the recovery process is performed only for grains that are actually being used and it is spread in time and across all available silos, which improves the responsiveness of the system and the speed of recovery.
Note also that there is a delay between the time when a silo fails and when the Orleans cluster detects the failure. The delay is a configurable tradeoff between the speed of detection and the probability of false positives. During this transition period all calls to the grain will fail, but after the detection of the failure the grain will be created, upon a new call to it, on another silo, so it will be eventually available. More information can be found [here](Runtime-Implementation-Details/Cluster-Management.md).

## What happens if a grain call takes too much time to execute?

Since Orleans uses a cooperative multi-tasking model, it will not preempt the execution of a grain automatically but Orleans generates warnings for long executing grain calls so you can detect them. Cooperative multi-tasking has a much better throughput compared to preemptive multi-tasking. You should keep in mind that grain calls should not execute any long running tasks like IO synchronously and should not block on other tasks to complete. All waiting should be done asynchronously using the `await` keyword or other asynchronous waiting mechanisms. Grains should return as soon as possible to let other grains execute for maximum throughput.

## In what cases can a split brain (same grain activated in multiple silos at the same time) happen?

This can never happen during normal operations and each grain will have one and only one instance per ID.
The only time this can occur is when a silo crashes or if it's killed without being allowed to properly shutdown.
In that case there is a 30-60 seconds window (based on configuration) where a grain can exist in multiple silos before one is removed from the system.
The mapping from grain IDs to their activation (instance) addresses is stored in a distributed directory (implemented with DHT) and each silo owns a partition of this directory. When membership views of two silos differ, both can request creation of an instance and as a result. two instances of the grain may co-exist. Once the cluster silos reach an agreement on membership, one of the instances will be deactivated and only one activation will survive.
You can find out more about how Orleans manages the clusters at [Cluster Management](Runtime-Implementation-Details/Cluster-Management.md) page.
Also you can take a look at Orleans's [paper](http://research.microsoft.com/pubs/210931/Orleans-MSR-TR-2014-41.pdf) for a more detailed information, however you don't need to understand it fully to be able to write your application code.
You just need to consider the rare possibility of having two instances of an actor while writing your application.
