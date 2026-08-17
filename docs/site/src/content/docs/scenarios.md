---
title: Orleans scenarios and use cases
description: Decide whether the Orleans virtual actor model fits an application's workload.
ms.date: 08/17/2026
ms.topic: conceptual
---

# Orleans scenarios and use cases

Orleans supports applications with many independently addressable entities whose state and work partition by identity. A grain owns the behavior and state for one entity, and the runtime manages its activation, placement, routing, and turn-based execution.

Choose grains according to identity, state ownership, concurrency, and call patterns. Combine them with databases, queues, stream processors, and compute services according to each workload.

## Signals that Orleans is a strong fit

Consider Orleans when several of these statements describe the application:

- Domain entities have stable identities, such as a player, device, account, order, room, tenant, or session.
- Each entity owns state or coordinates work over time.
- Requests for one entity benefit from serialized, turn-based execution.
- The workload contains many entities and can distribute traffic across their keys.
- Entities need timers, reminders, streams, persistence, or calls to other entities.
- Callers use a stable logical identity while the runtime resolves the entity's current host.
- The application is already a distributed .NET service, or is expected to grow into one.

Scalability comes from grain boundaries, key distribution, call patterns, storage, and external dependencies. Partition popular entities across multiple keys to distribute their work.

## Common scenarios

### Multiplayer games and presence

Players, game sessions, rooms, parties, and matches have natural identities and typically own mutable state. Grains can serialize operations for each entity, coordinate interactions through grain calls, and notify connected clients through observers or streams.

Use Orleans for authoritative game and social state, matchmaking coordination, presence, and session lifecycle. Keep latency-critical simulation, rendering, and other CPU-intensive loops in an execution model designed for that work.

See the [Adventure game](tutorials-and-samples/adventure.md) explanation and the maintained [Presence Service sample](https://github.com/dotnet/orleans/tree/main/samples/Presence).

### Devices and digital twins

A grain per device can maintain last-known state, apply commands in order, manage configuration, and coordinate periodic or scheduled work. Grain identity lets callers address devices directly while Orleans resolves their current hosts, and persistence restores explicitly written state after reactivation.

Grains can consume selected telemetry events and own the stateful behavior of each device. Brokers, time-series stores, and stream-processing systems provide high-volume ingestion, long-term retention, and fleet-wide analytics for the surrounding telemetry pipeline.

The [GPS Tracker sample](https://github.com/dotnet/orleans/tree/main/samples/GPSTracker) models devices as grains and integrates Orleans with ASP.NET Core SignalR.

### Commerce, accounts, and business processes

Shopping carts, orders, accounts, reservations, and similar entities often have clear ownership boundaries and rules which must hold for one key. A grain can keep those rules with the state they protect, persist changes deliberately, and use reminders or durable jobs for later work.

Orleans supports distributed ACID transactions for operations spanning supported transactional state across multiple entities. Idempotent commands, explicit coordination, and reconciliation support workflows whose consistency model permits independent state transitions.

See the [Shopping Cart sample](https://github.com/dotnet/orleans/tree/main/samples/ShoppingCart), the [Bank Account transactions sample](https://github.com/dotnet/orleans/tree/main/samples/BankAccount), and [Orleans transactions](grains/transactions.md).

### Collaboration, messaging, and live sessions

Rooms, channels, documents, users, and sessions can be independently addressed and can retain behavior between client requests. Grain observers fit transient callbacks to connected clients, while Orleans streams fit multicast event flows and subscriptions whose guarantees depend on the selected provider.

Use a dedicated broker or storage system when the primary requirement is durable message retention, competing consumers, large broadcast fan-out, or analytics over the full event history. Orleans can complement those systems by applying per-entity state and behavior to selected events.

The [Chat Room sample](https://github.com/dotnet/orleans/tree/main/samples/ChatRoom) combines a grain per channel with Orleans streams.

### AI agents and conversational sessions

An AI agent session is a natural grain identity. A session grain can own conversation state, model and tool configuration, pending work, and coordination with other agents or services. Orleans activates sessions on demand, removes idle activations, routes each request to the current activation, and processes turns one at a time by default. This provides lifecycle management and serializes concurrent session updates.

Model inference and tool calls are typically asynchronous external operations. A grain can await them efficiently, use [response streaming](grains/response-streaming.md) to return generated tokens or progress during a live invocation, and use timers, reminders, or durable jobs to schedule later work. A stream provider with retention and replay capabilities, or durable storage, can retain output for reconnection and later consumption.

After membership converges following a silo failure, a later request can reactivate the session on a healthy silo, and [grain persistence](grains/grain-persistence/index.md) restores state written to durable storage. Explicit durability points, idempotent tool calls, bounded retries, and reconciliation preserve application-level outcomes across failures.

### Per-entity orchestration

Long-lived processes such as a bot conversation, user workflow, subscription, or scheduled campaign can map to grains when each instance has an identity and progresses independently. Grain state records progress, grain calls express coordination, and timers, reminders, or durable jobs trigger later work.

Orleans expresses programmatic, per-entity orchestration in grain code. Workflow engines provide visual process authoring, human approval queues, and queryable audit histories, and can integrate with grains which own domain state.

## Choose the architecture by workload

Match the primary workload to the architecture designed for its execution and state model:

- **Stateless request processing or straightforward CRUD.** An ASP.NET Core service backed by a database provides request handling, coordination, and durability.
- **A few large CPU-bound or data-parallel jobs.** Parallel compute, batch, or job-processing tools distribute substantial computation. Grain turns remain short and asynchronous.
- **Bulk analytics, ETL, or declarative stream processing.** Databases and data-flow engines are designed for scans, joins, windows, and shared transformations across large data sets.
- **One globally coordinated resource or a permanently hot key.** Domain partitioning distributes the work, while systems specialized for single-resource coordination manage a centralized access pattern.
- **Shared-memory or hard real-time processing.** A local concurrent runtime provides shared-memory access, and a hard real-time platform provides deterministic scheduling and bounded latency.
- **A work queue with competing consumers.** A competing-consumer queue assigns each item to one worker. Orleans streams deliver each item to every subscription on a logical stream.

Orleans commonly owns the stateful entity subsystem alongside HTTP APIs, databases, caches, brokers, compute workers, and analytics systems.

## Evaluate the model against the workload

Before committing to a design, identify candidate grain keys and test the busiest paths:

1. Define which entity owns each invariant and operation.
1. Estimate the number of active keys and the traffic distribution between them.
1. Look for hot keys, global coordinators, chatty call chains, and large messages.
1. Decide what state must survive failure and when writes must complete.
1. Define retry, idempotency, timeout, and recovery behavior for each external operation.
1. Load test a representative key distribution and the production provider types.

For the programming model's benefits and tradeoffs, see [Why Orleans](benefits.md). For design and operational guidance, see [Orleans best practices](resources/best-practices.md) and the [production-readiness checklist](deployment/production-readiness.md).
