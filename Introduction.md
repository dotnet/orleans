---
layout: page
title: Introduction
---
{% include JB/setup %}

Orleans is a framework that provides a straightforward approach to building distributed high-scale computing applications, without the need to learn and apply complex concurrency or other scaling patterns. It was created by Microsoft Research and designed for use in the cloud. Orleans has been used extensively in Microsoft Azure by several Microsoft product groups, most notably by 343 Industries as a platform for all of Halo 4 cloud services, as well as by a number of other companies.

#### Background

Cloud applications and services are inherently parallel and distributed. They are also interactive and dynamic; often requiring near real time direct interactions between cloud entities. Such applications are very difficult to build today. The development process demands expert level programmers and typically requires expensive iterations of the design and the architecture, as the workload grows.

Most of today’s high scale properties are built with the SOA paradigm. Rendering of a single web page by Amazon or Google or Facebook involves complex interactions of hundreds of SOA services that are independently built, deployed and managed. The fact that each individual service scales well by itself does not guarantee scalability of a composition of such services.

The data scale-out mechanism of SOA is partitioning. As data size and load grow and “hot spots” come and go, a service has to dynamically repartition its state and do so without interrupting its operation. SOA challenges the programmer with a high degree of concurrency of requests within partitions. But existing tools do not provide good support for safe and efficient concurrency and distributed parallelism.

The stateless N-tier model delegates the partitioning problem to the storage layer. It often requires caching in the stateless layer to get acceptable performance, adding complexity and introducing cache consistency issues.

#### Actors

The actor model supports fine-grain individual objects—actors—that are isolated from each other and light-weight enough to allow modeling of an individual entity as an actor. They communicate via asynchronous message passing, which enables direct communications between actors.

Significantly, an actor executes with single-threaded semantics. Coupled with encapsulation of the actor’s state and isolation from other actors, this simplifies writing highly concurrent systems by removing data races from the actor’s code level. Developers using actors do not have to worry about critical regions, mutexes, lock leveling, and other complex race-prevention concerns that have nothing to do with the actual application logic. Actors are dynamically created within the pool of available hardware resources. This makes balancing of load easier compared to hash-based partitioning of SOA.

For the last decade, Erlang has been the most popular implementation of the traditional actor model. Facing the above-mentioned challenges of SOA, the industry started rediscovering the actor model, which stimulated renewed interest in Erlang and creation of new Erlang-like solutions: Scala actors, Akka, DCell.

#### Project "Orleans"

The project we have codenamed "Orleans" is an implementation of an improved actor model that borrows heavily from Erlang and distributed objects systems, adds static typing, message indirection and actor virtualization, exposing them in an integrated programming model. Whereas Erlang is a pure functional language with its own custom VM, the "Orleans" programming model directly leverages .NET and its object-oriented capabilities.

Project "Orleans" has been used in production within Microsoft since fall 2012, serving as the programming model for the Halo Reach and Halo 4 backend services.

[Read the MSR Technical Report on Orleans](http://research.microsoft.com/pubs/210931/Orleans-MSR-TR-2014-41.pdf)

The main benefits of "Orleans" are: **1) developer productivity**, even for non-expert programmers; and **2) transparent scalability by default** with no special effort from the programmer. It is a set of .NET libraries and tools that make development of complex distributed applications much easier and make the resulting applications scalable by design. We expand on each of these benefits below.

#### Developer Productivity

The "Orleans" programming model raises productivity of both expert and non-expert programmers by providing the following key abstractions, guarantees and system services.

* **Familiar object-oriented programming (OOP) paradigm**. Actors are .NET classes that implement declared .NET actor interfaces with asynchronous methods and properties. Thus actors appear to the programmer as remote objects whose methods/properties can be directly invoked. This provides the programmer the familiar OOP paradigm by turning method calls into messages, routing them to the right endpoints, invoking the target actor’s methods and dealing with failures and corner cases in a completely transparent way.
* **Single-threaded execution of actors**. The runtime guarantees that an actor never executes on more than one thread at a time. Combined with the isolation from other actors, the programmer never faces concurrency at the actor level, and hence never needs to use locks or other synchronization mechanisms to control access to shared data. This feature alone makes development of distributed applications tractable for non-expert programmers.
* **Transparent activation**. The runtime activates an actor as-needed, only when there is a message for it to process. This cleanly separates the notion of creating a reference to an actor, which is visible to and controlled by application code, and physical activation of the actor in memory, which is transparent to the application. In many ways, this is similar to virtual memory in that it decides when to “page out” (deactivate) or “page in” (activate) an actor; the application has uninterrupted access to the full “memory space” of logically created actors, whether or not they are in the physical memory at any particular point in time. Transparent activation enables dynamic, adaptive load balancing via placement and migration of actors across the pool of hardware resources. This features is a significant improvement on the traditional actor model, in which actor lifetime is application-managed.
* **Location transparency**. An actor reference (proxy object) that the programmer uses to invoke the actor’s methods or pass to other components only contains the logical identity of the actor. The translation of the actor’s logical identity to its physical location and the corresponding routing of messages are done transparently by the "Orleans" runtime. Application code communicates with actors oblivious to their physical location, which may change over time due to failures or resource management, or because an actor is deactivated at the time it is called.
* **Transparent integration with persistent store**. "Orleans" allows for declarative mapping of actors’ in-memory state to persistent store. It synchronizes updates, transparently guaranteeing that callers receive results only after the persistent state has been successfully updated. Extending and/or customizing the set of existing persistent storage providers available is straight-forward.
* **Automatic propagation of errors**. The runtime automatically propagates unhandled errors up the call chain with the semantics of asynchronous and distributed try/catch. As a result, errors do not get lost within an application. This allows the programmer to put error handling logic at the appropriate places, without the tedious work of manually propagating errors at each level.

#### Transparent Scalability by Default

The "Orleans" programming model is designed to guide the programmer down a path of likely success in scaling their application or service through several orders of magnitude. This is done by incorporating the proven best practices and patterns, and providing an efficient implementation of the lower level system functionality. Here are some key factors that enable scalability and performance.

* **Implicit fine grain partitioning of application state**. By using actors as directly addressable entities, the programmer implicitly breaks down the overall state of their application. While the "Orleans" programming model does not prescribe how big or small an actor should be, in most cases it makes sense to have a relative large number of actors – millions or more – with each representing a natural entity of the application, such as a user account, a purchase order, etc. With actors being individually addressable and their physical location abstracted away by the runtime, "Orleans" has enormous flexibility in balancing load and dealing with hot spots in a transparent and generic way without any thought from the application developer.
* **Adaptive resource management**. With actors making no assumption about locality of other actors they interact with and because of the location transparency, the runtime can manage and adjust allocation of available HW resources in a very dynamic way by making fine grain decisions on placement/migration of actors across the compute cluster in reaction to load and communication patterns without failing incoming requests. By creating multiple replicas of a particular actor the runtime can increase throughput of the actor if necessary without making any changes to the application code.
* **Multiplexed communication**. Actors in "Orleans" have logical endpoints, and messaging between them is multiplexed across a fixed set of all-to-all physical connections (TCP sockets). This allows the  runtime to host a very large number (millions) of addressable entities with low OS overhead per actor. In addition, activation/deactivation of an actor does not incur the cost of registering/unregistering of a physical endpoint, such as a TCP port or a HTTP URL, or even closing a TCP connection.
* **Efficient scheduling**. The runtime schedules execution of a large number of single-threaded actors across a custom thread pool with a thread per physical processor core. With actor code written in the non-blocking continuation based style (a requirement of the Orleans programming model) application code runs in a very efficient “cooperative” multi-threaded manner with no contention. This allows the system to reach high throughput and run at very high CPU utilization (up to 90%+) with great stability. The fact that a growth in the number of actors in the system and the load does not lead to additional threads or other OS primitives helps scalability of individual nodes and the whole system.
* **Explicit asynchrony**. The "Orleans" programming model makes the asynchronous nature of a distributed application explicit and guides programmers to write non-blocking asynchronous code. Combined with asynchronous messaging and efficient scheduling, this enables a large degree of distributed parallelism and overall throughput without the explicit use of multi-threading.

