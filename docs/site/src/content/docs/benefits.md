---
title: Why Orleans
description: Understand the benefits and tradeoffs of the Orleans virtual actor model.
ms.date: 08/02/2026
ms.topic: overview
---

# Why Orleans

Orleans helps .NET developers build distributed, stateful applications by combining a virtual actor programming model with runtime services for placement, messaging, lifecycle, and failure detection.

Its main benefit is not that distribution becomes invisible. Network calls can fail, storage must be configured, and workloads still need capacity planning. Orleans reduces the amount of application-specific infrastructure needed to address and coordinate large numbers of independent entities.

## Stable identities instead of locations

Application code addresses grains by interface and key. Orleans maps that logical identity to an activation and routes calls to it. Callers don't maintain a server registry or recreate references when placement changes.

Because identity is independent of activation, Orleans can activate grains on demand and remove idle activations from memory. Applications can represent far more logical entities than fit in memory at once.

## Isolated, turn-based execution

Each grain activation encapsulates its behavior and state. By default, it processes one request at a time. This makes per-entity invariants easier to reason about than shared-memory concurrency and removes the need for locks in typical grain code.

The boundary is explicit: grain calls are asynchronous and may cross a process or machine. That encourages APIs which account for latency, serialization, cancellation, and failure.

## Natural partitioning

Mapping domain entities to grains partitions state and work by identity. Orleans can place those activations across a cluster and route calls without callers knowing their locations.

This design works best when load is spread across many grain keys. A hot grain remains a hot grain: Orleans doesn't automatically replicate an ordinary stateful grain to increase its throughput. Applications should choose grain boundaries, stateless workers, aggregation hierarchies, or other patterns appropriate to the workload.

## Managed activation lifecycle

Orleans activates grains when they receive calls and can deactivate idle activations to reclaim memory. Following a silo failure, later calls can reactivate grains on healthy silos after membership converges.

Activation recovery isn't the same as state replication. Volatile in-memory state is lost with the process. Durable recovery requires a configured storage provider and successful calls to the persistence APIs.

## Composable runtime services

Orleans provides consistent hosting and programming abstractions for:

- Cluster membership and client discovery.
- Activation placement and rebalancing.
- Grain persistence and transactions.
- Timers, reminders, and durable jobs.
- Streams and broadcast channels.
- Serialization, versioning, security, and observability.

Provider packages integrate these abstractions with infrastructure such as Azure Storage, Azure Cosmos DB, relational databases, DynamoDB, Redis, Event Hubs, SQS, NATS, Consul, Cassandra, ZooKeeper, and Kubernetes.

## Familiar .NET development

Grain contracts are .NET interfaces with asynchronous methods, grain implementations are classes, and hosts use the .NET generic host and dependency injection. Orleans also integrates with ASP.NET Core, logging, configuration, OpenTelemetry, Aspire, and standard testing tools.

The result is a distributed programming model that remains recognizably .NET while making location, lifecycle, and routing runtime concerns rather than application plumbing.

## Tradeoffs

Orleans is not the best fit for every workload. Consider another design when the application primarily needs:

- Shared mutable memory across components.
- A few large, highly parallel compute jobs.
- Global coordination on every request.
- Bulk data processing where entity identity provides little value.

For suitable domains, Orleans offers a productive default architecture. It doesn't remove distributed-systems tradeoffs, but it gives applications a tested foundation on which to address them.
