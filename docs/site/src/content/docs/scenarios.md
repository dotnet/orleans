---
title: Orleans scenarios and use cases
description: Decide whether the Orleans virtual actor model fits an application's workload.
ms.date: 08/15/2026
ms.topic: conceptual
---

# Orleans scenarios and use cases

Orleans is most useful when an application has many independently addressable entities whose state and work can be partitioned by identity. A grain can own the behavior and state for one entity while Orleans handles activation, placement, routing, and turn-based execution.

The domain name alone doesn't determine whether Orleans is a good fit. A game, device platform, or financial service can contain workloads which suit grains and workloads which are better handled by databases, queues, stream processors, or compute services.

## Signals that Orleans is a strong fit

Consider Orleans when several of these statements describe the application:

- Domain entities have stable identities, such as a player, device, account, order, room, tenant, or session.
- Each entity owns state or coordinates work over time instead of serving one stateless request.
- Requests for one entity benefit from serialized, turn-based execution.
- The workload contains many entities and can distribute traffic across their keys.
- Entities need timers, reminders, streams, persistence, or calls to other entities.
- Callers should address an entity without tracking which process currently hosts it.
- The application is already a distributed .NET service, or is expected to grow into one.

These signals aren't guarantees of scalability. Grain boundaries, key distribution, call patterns, storage, and external dependencies still determine capacity and failure behavior. A single popular grain remains a single hot key unless the application partitions its work.

## Common scenarios

### Multiplayer games and presence

Players, game sessions, rooms, parties, and matches have natural identities and typically own mutable state. Grains can serialize operations for each entity, coordinate interactions through grain calls, and notify connected clients through observers or streams.

Use Orleans for authoritative game and social state, matchmaking coordination, presence, and session lifecycle. Keep latency-critical simulation, rendering, and other CPU-intensive loops in an execution model designed for that work.

See the [Adventure game](tutorials-and-samples/adventure.md) explanation and the maintained [Presence Service sample](https://github.com/dotnet/orleans/tree/main/samples/Presence).

### Devices and digital twins

A grain per device can maintain last-known state, apply commands in order, manage configuration, and coordinate periodic or scheduled work. Grain identity avoids maintaining an application-level map from device IDs to servers, and persistence can restore explicitly written state after reactivation.

Orleans can participate in a telemetry pipeline, but sending every raw measurement through a grain isn't automatically the best design. High-volume ingestion, long-term retention, and fleet-wide analytics often belong in a broker, time-series store, or stream-processing system. Grains can consume selected events and own the stateful behavior of each device.

The [GPS Tracker sample](https://github.com/dotnet/orleans/tree/main/samples/GPSTracker) models devices as grains and integrates Orleans with ASP.NET Core SignalR.

### Commerce, accounts, and business processes

Shopping carts, orders, accounts, reservations, and similar entities often have clear ownership boundaries and rules which must hold for one key. A grain can keep those rules with the state they protect, persist changes deliberately, and use reminders or durable jobs for later work.

Some operations span multiple entities. Orleans supports distributed ACID transactions for supported transactional state, but transactions add storage and contention considerations and aren't required for every workflow. Applications can also use idempotent commands, explicit coordination, and reconciliation according to their consistency requirements.

See the [Shopping Cart sample](https://github.com/dotnet/orleans/tree/main/samples/ShoppingCart), the [Bank Account transactions sample](https://github.com/dotnet/orleans/tree/main/samples/BankAccount), and [Orleans transactions](grains/transactions.md).

### Collaboration, messaging, and live sessions

Rooms, channels, documents, users, and sessions can be independently addressed and can retain behavior between client requests. Grain observers fit transient callbacks to connected clients, while Orleans streams fit multicast event flows and subscriptions whose guarantees depend on the selected provider.

Use a dedicated broker or storage system when the primary requirement is durable message retention, competing consumers, large broadcast fan-out, or analytics over the full event history. Orleans can complement those systems by applying per-entity state and behavior to selected events.

The [Chat Room sample](https://github.com/dotnet/orleans/tree/main/samples/ChatRoom) combines a grain per channel with Orleans streams.

### Per-entity orchestration

Long-lived processes such as a bot conversation, user workflow, subscription, or scheduled campaign can map to grains when each instance has an identity and progresses independently. Grain state records progress, grain calls express coordination, and timers, reminders, or durable jobs trigger later work.

Orleans isn't a general-purpose workflow product. If the primary requirements are visual process authoring, human approval queues, or a queryable audit history supplied by a workflow engine, use that system directly or integrate it with Orleans.

## When another approach may fit better

Another design is usually simpler when the workload primarily consists of:

- **Stateless request processing or straightforward CRUD.** An ASP.NET Core service backed by a database may provide all the required coordination and durability.
- **A few large CPU-bound or data-parallel jobs.** Grain turns should remain short and asynchronous. Use parallel compute, batch, or job-processing tools for substantial computation.
- **Bulk analytics, ETL, or declarative stream processing.** Databases and data-flow engines are designed for scans, joins, windows, and shared transformations across large data sets.
- **One globally coordinated resource or a permanently hot key.** Adding silos doesn't divide one grain's work. Partition the domain, accept the bottleneck, or choose a system designed for that access pattern.
- **Shared-memory or hard real-time processing.** Grain calls are asynchronous and can cross a network. Orleans doesn't provide shared mutable memory, deterministic scheduling, or hard real-time latency.
- **A work queue with competing consumers.** Orleans streams are multicast rather than a competing-consumer queue. Model ownership explicitly or use a queue designed for worker dispatch.

A system can use Orleans for one stateful subsystem without modeling every component as a grain. HTTP APIs, databases, caches, brokers, compute workers, and analytics systems commonly remain part of the architecture.

## Evaluate the model against the workload

Before committing to a design, identify candidate grain keys and test the busiest paths:

1. Define which entity owns each invariant and operation.
1. Estimate the number of active keys and the traffic distribution between them.
1. Look for hot keys, global coordinators, chatty call chains, and large messages.
1. Decide what state must survive failure and when writes must complete.
1. Define retry, idempotency, timeout, and recovery behavior for each external operation.
1. Load test a representative key distribution and the production provider types.

For the programming model's benefits and tradeoffs, see [Why Orleans](benefits.md). For design and operational guidance, see [Orleans best practices](resources/best-practices.md) and the [production-readiness checklist](deployment/production-readiness.md).
