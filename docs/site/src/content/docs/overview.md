---
title: Orleans overview
description: Learn how Orleans simplifies building distributed .NET applications.
ms.date: 08/02/2026
ms.topic: overview
---

# Microsoft Orleans

Orleans is a cross-platform framework for building distributed applications with .NET. It extends familiar C# concepts across a cluster so that application code can address stateful entities by identity without tracking which server currently hosts them.

Orleans libraries target `net8.0` and `net10.0`.

> [!NOTE]
> To upgrade an existing application, see the [Orleans migration guide](migration-guide.md). Version-specific upgrade history is kept in that guide rather than repeated in ordinary documentation.

## The virtual actor model

Orleans implements the virtual actor model. A virtual actor in Orleans is called a *grain*. Each grain has:

- A stable, application-defined identity.
- Behavior implemented by a .NET class.
- Isolated, mutable in-memory state, with optional durable state stored by a configured provider.

Grain references are logical addresses. Callers don't create grain objects directly or locate them on a server. Instead, they request a reference by interface and key, then invoke asynchronous methods on that reference. Orleans routes each call to an existing activation or activates the grain on demand.

This virtual lifecycle separates a grain's identity from its in-memory activation. Idle activations can be removed from memory, and a later call can activate the grain again. If durable state is configured and written by the grain, a new activation can restore that state.

For the model's design and history, see the [Orleans virtual actors research project](https://www.microsoft.com/research/project/orleans-virtual-actors/).

## Grains

Grains are units of identity, isolation, placement, and execution. A grain class implements one or more grain interfaces, usually identified using one of these marker interfaces:

- <xref:Orleans.IGrainWithGuidKey>
- <xref:Orleans.IGrainWithIntegerKey>
- <xref:Orleans.IGrainWithStringKey>
- <xref:Orleans.IGrainWithGuidCompoundKey>
- <xref:Orleans.IGrainWithIntegerCompoundKey>

By default, Orleans processes requests to a grain activation one at a time. This turn-based execution model reduces the need for locks inside grain code. Reentrancy and other interleaving options are available when an application needs them, but they require the same care as other concurrent code.

:::image type="content" source="media/grain-formulation.svg" lightbox="media/grain-formulation.svg" alt-text="A grain is composed of a stable identity, behavior, and state.":::

Orleans applications commonly model users, devices, accounts, game sessions, orders, or other independently addressable entities as grains. Good grain boundaries come from the application's domain and workload: avoid both chatty networks of tiny grains and single grains that become throughput bottlenecks.

## Silos and clusters

A *silo* is a process that hosts the Orleans runtime and grain activations. One or more silos form a cluster. Cluster members coordinate membership, route messages, and place activations.

:::image type="content" source="media/cluster-silo-grain-relationship.svg" lightbox="media/cluster-silo-grain-relationship.svg" alt-text="A cluster has one or more silos, and each silo hosts grain activations.":::

When a silo fails, calls in progress can fail and callers must handle that outcome. After the cluster detects the failure, subsequent calls can activate affected grains on healthy silos. Durable recovery depends on the application's storage configuration and on state having been written successfully.

External processes can connect using the Orleans client library. ASP.NET Core applications can also host a silo and call grains in the same process, which is the approach used in the beginner quickstart.

## Runtime capabilities

Orleans includes APIs and providers for common distributed application needs:

- **Clustering and placement**: Discover silos and choose where activations run.
- **Persistence**: Store explicitly written grain state using Azure Storage, Azure Cosmos DB, ADO.NET, DynamoDB, Redis, or custom providers.
- **Timers, reminders, and durable jobs**: Run activation-scoped callbacks, recurring durable callbacks, or scheduled one-time work.
- **Streams and broadcast channels**: Deliver events between producers and consumers.
- **Transactions**: Coordinate supported persistent state across grains using distributed ACID transactions.
- **Versioning**: Run heterogeneous clusters during staged upgrades.
- **Observability**: Integrate with .NET logging, metrics, distributed tracing, health checks, and the Orleans Dashboard.
- **Serialization**: Generate version-tolerant serializers and integrate with formats such as System.Text.Json, MessagePack, and Protocol Buffers.

These capabilities are composable. An application selects providers and guarantees that fit its environment; Orleans doesn't automatically replicate application state or make external storage durable.

## Distributed ACID transactions

Orleans transactions coordinate supported persistent state across multiple grains using distributed [ACID](https://learn.microsoft.com/windows/win32/cossdk/acid-properties) transactions with serializable isolation. Transactional state uses dedicated APIs and storage configuration; ordinary grain persistence doesn't become transactional automatically.

For more information, see [Transactions](grains/transactions.md).

## When to use Orleans

Orleans is a strong fit when an application has many independently addressable entities, benefits from per-entity isolation, and needs to distribute work across processes or machines. Common examples include AI agent sessions, games, device management, collaboration, financial workflows, shopping, and online services.

Orleans can run in a single process during development and scale to a cluster without changing grain interfaces. Production deployments still require deliberate choices for clustering, storage, retries, observability, capacity, and deployment.

For a decision guide with concrete examples and counterexamples, see [Orleans scenarios and use cases](scenarios.md).

## Next step

> [!div class="nextstepaction"]
> [Orleans Hello World](tutorials-and-samples/hello-world.md)
