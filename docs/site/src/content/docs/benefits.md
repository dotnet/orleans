---
title: Orleans benefits
description: Learn the many benefits of .NET Orleans.
ms.date: 05/23/2025
ms.topic: overview
---

# Orleans benefits

The main benefits of Orleans are:

- **Developer productivity**: Even for non-expert programmers.
- **Transparent scalability by default**: Requires no special effort from the developer.

## Developer productivity

The Orleans programming model raises productivity, regardless of expertise level, by providing the following key abstractions, guarantees, and system services.

### Familiar object-oriented programming (OOP) paradigm

Grains are .NET classes that implement declared .NET grain interfaces with asynchronous methods. Grains appear as remote objects whose methods can be directly invoked. This provides the familiar OOP paradigm by turning method calls into messages, routing them to the right endpoints, invoking the target grain's methods, and transparently handling failures and corner cases.

### Single-threaded execution of grains

The runtime guarantees a grain never executes on more than one thread at a time. Combined with isolation from other grains, developers never face concurrency at the grain level and never need locks or other synchronization mechanisms to control access to shared data. This feature alone makes developing distributed applications tractable, even for non-expert programmers.

### Transparent activation

The runtime activates a grain only when there's a message for it to process. This cleanly separates creating a grain reference (controlled by application code) and physical activation of the grain in memory (transparent to the application). This is similar to [virtual memory](https://wikipedia.org/wiki/Virtual_memory) where the OS decides when to bring pages into memory and when to evict pages from memory. Similarly, in Orleans, the runtime decides when to activate a grain (bringing it into memory) and when to deactivate a grain (evicting it from memory). The application has uninterrupted access to the full "memory space" of logically created grains, whether they are in physical memory at any given time.

Transparent activation enables dynamic, adaptive load balancing via placement and migration of grains across the pool of hardware resources. This feature significantly improves on the traditional actor model, where actor lifetime is application-managed.

### Location transparency

A grain reference (proxy object) that's used to invoke a grain's methods or pass to other components contains only the grain's logical identity. The Orleans runtime transparently handles translating the grain's logical identity to its physical location and routing messages accordingly.

Application code communicates with grains without knowing their physical location. This location might change over time due to failures, resource management, or because a grain is deactivated when called.

### Transparent integration with a persistent store

Orleans allows declaratively mapping a grain's in-memory state to a persistent store. It synchronizes updates, transparently guaranteeing callers receive results only after the persistent state successfully updates. Extending and/or customizing the set of existing persistent storage providers is straightforward.

### Automatic propagation of errors

The runtime automatically propagates unhandled errors up the call chain with the semantics of asynchronous and distributed try/catch. As a result, errors don't get lost within an application. This allows placing error handling logic in appropriate places without the tedious work of manually propagating errors at each level.

## Transparent scalability by default

The Orleans programming model guides developers toward successfully scaling applications or services through several orders of magnitude. It achieves this by incorporating proven best practices and patterns and providing an efficient implementation of lower-level system functionality.

Here are some key factors enabling scalability and performance:

### Implicit fine-grained partitioning of application state

Using grains as directly addressable entities implicitly breaks down the application's overall state. While the Orleans programming model doesn't prescribe grain size, in most cases, having a relatively large number of grains (millions or more) makes sense, with each representing a natural application entity, such as a user account or purchase order.

With grains being individually addressable and their physical location abstracted by the runtime, Orleans has enormous flexibility in balancing load and dealing with hot spots transparently and generically, without requiring thought from the application developer.

### Adaptive resource management

Grains don't assume the locality of other grains when interacting. Because of this location transparency, the runtime can dynamically manage and adjust the allocation of available hardware resources. The runtime achieves this by making fine-grained decisions on placing and migrating grains across the compute cluster in reaction to load and communication patterns — without failing incoming requests. By creating multiple replicas of a particular grain, the runtime can increase its throughput without changing application code.

### Multiplexed communication

Grains in Orleans have logical endpoints, and messaging among them is multiplexed across a fixed set of all-to-all physical connections (TCP sockets). This allows the runtime to host millions of addressable entities with low OS overhead per grain. Additionally, activating and deactivating a grain doesn't incur the cost of registering/unregistering a physical endpoint (like a TCP port or HTTP URL) or even closing a TCP connection.

### Efficient scheduling

The runtime schedules the execution of many single-threaded grains using the .NET Thread Pool, which is highly optimized for performance. When grain code is written in the non-blocking, continuation-based style (a requirement of the Orleans programming model), the application code runs very efficiently in a "cooperative" multi-threaded manner with no contention. This allows the system to achieve high throughput and run at very high CPU utilization (up to 90%+) with great stability.

The fact that growth in the number of grains and increased load doesn't lead to additional threads or other OS primitives helps the scalability of individual nodes and the whole system.

### Explicit asynchrony

The Orleans programming model makes the asynchronous nature of distributed applications explicit and guides developers to write non-blocking asynchronous code. Combined with asynchronous messaging and efficient scheduling, this enables a large degree of distributed parallelism and overall throughput without requiring explicit multi-threading.
