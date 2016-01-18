---
layout: page
title: Introduction
---
{% include JB/setup %}

Orleans is a framework that provides a straightforward approach to building distributed high-scale computing applications, without the need to learn and apply complex concurrency or other scaling patterns. It was created by Microsoft Research and designed for use in the cloud. Orleans has been used extensively in Microsoft Azure by several Microsoft product groups, most notably by 343 Industries as a platform for all of Halo 4 cloud services, as well as by a number of other companies.

### Background

Cloud applications and services are inherently parallel and distributed. They are also interactive and dynamic; often requiring near real time direct interactions between cloud entities. Such applications are very difficult to build today. The development process demands expert level programmers and typically requires expensive iterations of the design and the architecture, as the workload grows.

Most of today’s high scale properties are built with the SOA paradigm. Rendering of a single web page by Amazon or Google or Facebook involves complex interactions of hundreds of SOA services that are independently built, deployed and managed. The fact that each individual service scales well by itself does not guarantee scalability of a composition of such services.

The data scale-out mechanism of SOA is partitioning. As data size and load grow and “hot spots” come and go, a service has to dynamically repartition its state and do so without interrupting its operation. SOA challenges the programmer with a high degree of concurrency of requests within partitions. But existing tools do not provide good support for safe and efficient concurrency and distributed parallelism.

The stateless N-tier model delegates the partitioning problem to the storage layer. It often requires caching in the stateless layer to get acceptable performance, adding complexity and introducing cache consistency issues.

### Actors

The actor model supports fine-grain individual objects—actors—that are isolated from each other and light-weight enough to allow modeling of an individual entity as an actor. They communicate via asynchronous message passing, which enables direct communications between actors.

Significantly, an actor executes with single-threaded semantics. Coupled with encapsulation of the actor’s state and isolation from other actors, this simplifies writing highly concurrent systems by removing data races from the actor’s code level. Developers using actors do not have to worry about critical regions, mutexes, lock leveling, and other complex race-prevention concerns that have nothing to do with the actual application logic. Actors are dynamically created within the pool of available hardware resources. This makes balancing of load easier compared to hash-based partitioning of SOA.

For the last decade, Erlang has been the most popular implementation of the traditional actor model. Facing the above-mentioned challenges of SOA, the industry started rediscovering the actor model, which stimulated renewed interest in Erlang and creation of new Erlang-like solutions: Scala actors, Akka, DCell.

### Orleans

Orleans is an implementation of an improved actor model that borrows heavily from Erlang and distributed objects systems, adds static typing, message indirection and actor virtualization, exposing them in an integrated programming model. Whereas Erlang is a pure functional language with its own custom VM, the Orleans programming model directly leverages .NET and its object-oriented capabilities.
It provides a framework that makes development of complex distributed applications much easier and make the resulting applications scalable by design.

Orleans has been used in production within Microsoft since fall 2012, serving as the programming model for the Halo Reach and Halo 4 backend services.

[Read the MSR Technical Report on Orleans](http://research.microsoft.com/pubs/210931/Orleans-MSR-TR-2014-41.pdf)