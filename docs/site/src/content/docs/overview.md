---
title: Orleans overview
description: An introduction to .NET Orleans.
ms.date: 01/20/2026
ms.topic: overview
zone_pivot_groups: orleans-version
---

# Microsoft Orleans

Orleans is a cross-platform framework designed to simplify building distributed apps. Whether scaling from a single server to thousands of cloud-based apps, Orleans provides tools to help manage the complexities of distributed systems. It extends familiar C# concepts to multi-server environments, allowing developers to focus on the app's logic.

Here's what Orleans offers:

- It's designed to scale elastically. Add or remove servers, and Orleans adjusts accordingly to maintain fault tolerance and scalability.
- It simplifies distributed app development with a common set of patterns and APIs, making it accessible even for those new to distributed systems.
- It's cloud-native and runs on platforms where .NET is supported—Linux, Windows, macOS, and more.
- It supports modern deployment options like Kubernetes, Azure App Service, and Azure Container Apps.

Orleans is often referred to as "Distributed .NET" because of its focus on building resilient, scalable cloud-native services.

:::zone target="docs" pivot="orleans-10-0"

## What's new in Orleans 10.0

Orleans 10.0 introduces several new features and improvements:

- **Orleans Dashboard**: A built-in dashboard for real-time monitoring of your Orleans cluster, grains, and silos.
- **Redis Providers**: Stable Redis providers for clustering, persistence, and reminders.
- **CancellationToken support for system targets**: Extended cancellation token support throughout the framework.

> [!TIP]
> Orleans 10.0 targets .NET 8.0, .NET 9.0, and .NET 10.0.

:::zone-end

:::zone target="docs" pivot="orleans-9-0"

## What's new in Orleans 9.x

Orleans 9.x introduces several significant features:

- **Full CancellationToken support**: Grain methods now fully support cancellation tokens.
- **Strong-Consistency Grain Directory**: A new distributed in-memory directory with strong consistency guarantees.
- **Memory-Based Activation Shedding**: Automatic deactivation of grains under memory pressure.
- **In-Process Test Cluster**: Simplified testing infrastructure for Orleans applications.
- **Improved Membership Protocol**: Faster failure detection (90 seconds vs 10 minutes in earlier versions).
- **Activation Rebalancing**: Automatic grain rebalancing across silos (stable, experimental in 8.2).
- **Default Placement Change (9.2)**: Default placement strategy changed from Random to ResourceOptimized.

> [!TIP]
> Orleans 9.x targets .NET 8.0 and .NET 9.0.

:::zone-end

:::zone target="docs" pivot="orleans-8-0"

## What's new in Orleans 8.x

Orleans 8.x introduced several important features:

- **[Aspire Integration](host/aspire-integration.md)**: First-class support for Aspire for simplified cloud-native development.
- **Resource-Optimized Placement**: A new placement strategy based on CPU and memory utilization.
- **Activation Repartitioning (8.2, Experimental)**: Automatic redistribution of grains for improved performance.
- **New Grain Timer API (8.2)**: <xref:Orleans.GrainBaseExtensions.RegisterGrainTimer*> replaces the deprecated `RegisterTimer` method.
- **MessagePack Serializer (8.2)**: A new high-performance serialization option.
- **Cassandra Clustering Provider (8.2)**: Support for Apache Cassandra as a clustering provider.

> [!TIP]
> Orleans 8.x targets .NET 8.0.

:::zone-end

:::zone target="docs" pivot="orleans-7-0"

## What's new in Orleans 7.0

Orleans 7.0 was a major release with significant improvements:

- **Simplified APIs**: Streamlined builder patterns using <xref:Microsoft.Extensions.Hosting.GenericHostExtensions.UseOrleans*> and <xref:Microsoft.Extensions.Hosting.OrleansClientGenericHostExtensions.UseOrleansClient*>.
- **Source Generators**: Replaced code generation with source generators for better build-time performance.
- **New Serialization System**: A new high-performance, version-tolerant serialization framework.
- **IAsyncEnumerable Support (7.2)**: Streaming responses from grain methods.
- **Per-call Timeouts (7.2.2)**: Specify timeouts on individual grain calls.
- **Cosmos DB Providers (7.2)**: Azure Cosmos DB support for clustering, persistence, and reminders.

> [!TIP]
> Orleans 7.0 targets .NET 7.0 and later.

:::zone-end

:::zone target="docs" pivot="orleans-3-x"

## Orleans 3.x (Legacy)

Orleans 3.x is a legacy version. For new projects, consider upgrading to Orleans 7.0 or later to take advantage of improved performance, simplified APIs, and new features.

Key differences from newer versions:

- Uses <xref:Orleans.ClientBuilder> and <xref:Orleans.Hosting.SiloHostBuilder> instead of the unified <xref:Microsoft.Extensions.Hosting.GenericHostExtensions.UseOrleans*> pattern.
- Requires `ConfigureApplicationParts()` for assembly scanning.
- Uses the legacy code generator instead of source generators.

For migration guidance, see the [Migration guide](migration-guide.md).

:::zone-end

## The actor model

Orleans is based on the actor model. Originating in the early 1970s, the actor model is now a core component of Orleans. In the actor model, each _actor_ is a lightweight, concurrent, immutable object encapsulating a piece of state and corresponding behavior. Actors communicate exclusively using asynchronous messages. Orleans notably invented the _Virtual Actor_ abstraction, where actors exist perpetually.

> [!NOTE]
> Actors are purely logical entities that _always_ exist, virtually. An actor cannot be explicitly created nor destroyed, and its virtual existence is unaffected by the failure of a server that executes it. Since actors always exist, they are always addressable.

This novel approach helps build a new generation of distributed apps for the Cloud era. The Orleans programming model tames the complexity inherent in highly parallel distributed apps _without_ restricting capabilities or imposing constraints.

For more information, see [Orleans: Virtual Actors](https://www.microsoft.com/research/project/orleans-virtual-actors) via Microsoft Research. A virtual actor is represented as an Orleans grain.

## What are grains?

The grain is one of several Orleans primitives. In terms of the actor model, a grain is a virtual actor. The fundamental building block in any Orleans application is a *grain*. Grains are entities comprising user-defined identity, behavior, and state. Consider the following visual representation of a grain:

:::image type="content" source="media/grain-formulation.svg" lightbox="media/grain-formulation.svg" alt-text="A grain is composed of a stable identity, behavior, and state.":::

Grain identities are user-defined keys, making grains always available for invocation. Other grains or any number of external clients can invoke grains. Each grain is an instance of a class implementing one or more of the following interfaces:

- <xref:Orleans.IGrainWithGuidKey>: Marker interface for grains with <xref:System.Guid> keys.
- <xref:Orleans.IGrainWithIntegerKey>: Marker interface for grains with `Int64` keys.
- <xref:Orleans.IGrainWithStringKey>: Marker interface for grains with `string` keys.
- <xref:Orleans.IGrainWithGuidCompoundKey>: Marker interface for grains with compound keys.
- <xref:Orleans.IGrainWithIntegerCompoundKey>: Marker interface for grains with compound keys.

Grains can have volatile or persistent state data stored in any storage system. As such, grains implicitly partition application states, enabling automatic scalability and simplifying recovery from failures. Orleans keeps the grain state in memory while the grain is active, leading to lower latency and less load on data stores.

:::image type="content" source="media/grain-lifecycle.svg" lightbox="media/grain-lifecycle.svg" alt-text="The managed lifecycle of an Orleans grain.":::

The Orleans runtime automatically instantiates grains on demand. Grains not used for a while are automatically removed from memory to free up resources. This removal is possible due to their stable identity, allowing invocation of grains whether they're loaded into memory or not. This also enables transparent recovery from failure because the caller doesn't need to know on which server a grain is instantiated at any point. Grains have a managed lifecycle, with the Orleans runtime responsible for activating/deactivating and placing/locating grains as needed. This allows writing code as if all grains are always in memory.

## What are silos?

A silo is another example of an Orleans primitive. A silo hosts one or more grains. The Orleans runtime implements the programming model for applications.

Typically, a group of silos runs as a cluster for scalability and fault tolerance. When run as a cluster, silos coordinate to distribute work and detect and recover from failures. The runtime enables grains hosted in the cluster to communicate as if they are within a single process. To help visualize the relationship between clusters, silos, and grains, consider the following diagram:

:::image type="content" source="media/cluster-silo-grain-relationship.svg" lightbox="media/cluster-silo-grain-relationship.svg" alt-text="A cluster has one or more silos, and a silo has one or more grains.":::

The preceding diagram shows the relationship between clusters, silos, and grains. There can be any number of clusters, each cluster has one or more silos, and each silo has one or more grains.

In addition to the core programming model, silos provide grains with runtime services such as timers, reminders (persistent timers), persistence, transactions, streams, and more. For more information, see [What can be done with Orleans?](#what-can-be-done-with-orleans).

Web apps and other external clients call grains in the cluster using the client library, which automatically manages network communication. Clients can also be co-hosted in the same process with silos for simplicity.

## What can be done with Orleans?

Orleans is a framework for building cloud-native apps and should be considered when building .NET apps that might eventually need to scale. There are seemingly endless ways to use Orleans, but the following are some of the most common: Gaming, Banking, Chat apps, GPS tracking, Stock trading, Shopping carts, Voting apps, and more. Microsoft uses Orleans in Azure, Xbox, Skype, Halo, PlayFab, Gears of War, and many other internal services. Orleans has many features making it easy to use for various applications.

### Persistence

Orleans provides a simple persistence model ensuring state availability before processing a request and maintaining consistency. Grains can have multiple named persistent data objects. For example, one might be called "profile" for a user's profile and another "inventory" for their inventory. This state can be stored in any storage system.

While a grain runs, Orleans keeps the state in memory to serve read requests without accessing storage. When the grain updates its state, calling <xref:Orleans.Core.IStorage.WriteStateAsync*?displayProperty=nameWithType> ensures the backing store updates for durability and consistency.

For more information, see [Grain persistence](grains/grain-persistence/index.md).

### Timers and reminders

Reminders are a durable scheduling mechanism for grains. Use them to ensure an action completes at a future point, even if the grain isn't currently activated. Timers are the non-durable counterpart to reminders and can be used for high-frequency events not requiring reliability.

For more information, see [Timers and reminders](grains/timers-and-reminders.md).

### Flexible grain placement

When a grain activates in Orleans, the runtime decides which server (silo) to activate it on. This process is called grain placement.

The placement process in Orleans is fully configurable. Choose from out-of-the-box placement policies such as random, prefer-local, and load-based, or configure custom logic. This allows full flexibility in deciding where grains are created. For example, place grains on a server close to resources they need to operate against or other grains they communicate with.

For more information, see [Grain placement](grains/grain-placement.md).

### Grain versioning and heterogeneous clusters

Upgrading production systems safely accounting for changes can be challenging, particularly in stateful systems. To account for this, grain interfaces can be versioned in Orleans.

The cluster maintains a mapping of which grain implementations are available on which silos and their versions. The runtime uses this version information with placement strategies to make placement decisions when routing calls to grains. Besides safely updating a versioned grain, this also enables heterogeneous clusters where different silos have different sets of available grain implementations.

For more information, see [Grain Versioning](grains/grain-versioning/grain-versioning.md).

### Stateless workers

Stateless workers are specially marked grains without associated state that can activate on multiple silos simultaneously. This enables increased parallelism for stateless functions.

For more information, see [stateless worker grains](grains/stateless-worker-grains.md).

### Grain call filters

A [grain call filter](grains/interceptors.md) is logic common to many grains. Orleans supports filters for both incoming and outgoing calls. Common uses include authorization, logging and telemetry, and error handling.

### Request context

Pass metadata and other information with a series of requests using the [request context](grains/request-context.md). Use the request context for holding distributed tracing information or any other defined values.

### Distributed ACID transactions

Besides the simple persistence model described above, grains can have *transactional state*. Multiple grains can participate in [ACID](/windows/win32/cossdk/acid-properties) transactions together, regardless of where their state is ultimately stored. Transactions in Orleans are distributed and decentralized (meaning no central transaction manager or coordinator) and have [serializable isolation](https://en.wikipedia.org/wiki/Isolation_(database_systems)#Isolation_levels).

For more information on transactions, see [Transactions](grains/transactions.md).

### Streams

Streams help process a series of data items in near-real-time. Orleans streams are *managed*; streams don't need creating or registering before a grain or client publishes or subscribes. This allows greater decoupling of stream producers and consumers from each other and the infrastructure.

Stream processing is reliable: grains can store checkpoints (cursors) and reset to a stored checkpoint during activation or any subsequent time. Streams support batch delivery of messages to consumers to improve efficiency and recovery performance.

Streams are backed by queueing services such as Azure Event Hubs, Amazon Kinesis, and others.

An arbitrary number of streams can be multiplexed onto a smaller number of queues, and the responsibility for processing these queues is balanced evenly across the cluster.

## Introduction to Orleans video

For a video introduction to Orleans, check out the following video:

<!-- markdownlint-disable-next-line MD034 -->
> [!VIDEO https://aka.ms/docs/player?show=reactor&ep=an-introduction-to-orleans]

## Next steps

> [!div class="nextstepaction"]
> [Tutorial: Create a minimal Orleans application](tutorials-and-samples/tutorial-1.md)
