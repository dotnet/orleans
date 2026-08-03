---
title: Orleans best practices
description: Design, implement, and operate Orleans applications effectively.
ms.date: 08/02/2026
ms.topic: best-practice
---

# Orleans best practices

Orleans works best when an application models many independently addressable entities whose state and work can be partitioned by key. These practices are starting points; measure representative workloads and adjust the design to the application's domain.

## Model grains around domain ownership

- Give each grain a clear identity and responsibility, such as a user, account, device, order, room, or session.
- Keep invariants which must change together in the same grain when practical.
- Avoid networks of tiny grains which make several calls to complete one operation.
- Avoid singleton coordinator grains on high-throughput paths. Partition coordination by key or use staged aggregation.
- Don't assume that grain references imply locality. A grain call can cross a process or machine.

An ordinary stateful grain has one activation in the cluster unless its placement or registration says otherwise. Orleans doesn't automatically create replicas of it to increase throughput. Use partitioned identities, stateless workers, read replicas designed at the application layer, or another appropriate pattern.

## Keep grain turns short and asynchronous

- Don't block threads with `.Wait()`, `.Result`, synchronous I/O, sleeps, or locks.
- Await I/O and grain calls.
- Avoid CPU-intensive loops in a grain turn. Move substantial parallel computation to an appropriate worker or compute service.
- Bound fan-out and concurrency instead of creating an unbounded number of calls.
- Pass cancellation tokens where the contract supports cancellation, and treat cancellation as cooperative.

Grain activations process one turn at a time by default. Reentrant grains and call-chain reentrancy can interleave turns, so don't enable them merely to avoid a call-cycle problem. First simplify the call graph; if interleaving is intentional, document and test the invariants which span `await` points.

## Design grain APIs for a network

- Prefer coarse operations which express domain intent over chatty property-style APIs.
- Keep arguments and return values reasonably sized.
- Treat every grain call as fallible. Calls can time out or fail because of membership changes, process failures, overload, serialization, or application exceptions.
- Design retries around idempotency. Orleans provides at-most-once message delivery by default, not exactly-once processing.
- Avoid retrying indefinitely inside several layers. Establish bounded, observable retry policy at an appropriate boundary.

## Persist state deliberately

Use <xref:Orleans.Runtime.IPersistentState`1> or another supported state model and select a provider appropriate to the durability, consistency, latency, and operational requirements.

- Changing an in-memory state object doesn't write it automatically. Call the persistence API and await it before reporting success when durability is part of the operation's contract.
- Handle storage failures explicitly. A failed write means the requested durability wasn't achieved.
- Keep serialized types version tolerant. Use `[GenerateSerializer]` and stable `[Id]` values; don't reuse or renumber existing field IDs.
- Keep state objects small enough for the provider's item and request limits.
- Don't use memory storage when state must survive process loss or be shared by multiple silos.
- Don't rely on a silo's local file system as shared production storage.

Persistence doesn't replicate an activation's in-memory state. Recovery after process failure depends on state having been written to a durable, available provider.

## Choose production providers as a set

A multi-silo deployment needs shared cluster membership. Most production applications also need deliberate choices for grain storage, reminders, streams, durable jobs, and grain directories.

- Keep `ServiceId` stable for an application and use `ClusterId` to distinguish deployments which must not join each other.
- Use separate deployment identities for development, test, staging, and production.
- Follow the provider's authentication, encryption, backup, capacity, and high-availability guidance.
- Test provider throttling, transient failures, and regional outages.
- Don't expose silo or gateway endpoints directly to untrusted public clients. Put an authenticated application protocol such as HTTPS in front of the cluster.

For local development, localhost clustering and in-memory providers are convenient. They don't simulate all failure modes or guarantees of production infrastructure.

## Plan for membership changes

Silos can join, leave, restart, or fail at any time.

- Run enough silos and infrastructure replicas to meet the application's availability goals.
- Expect in-flight calls to fail during a silo failure. After membership converges, later calls can reactivate grains on healthy silos.
- Use rolling deployment and grain versioning features when contracts or implementations change.
- Understand the configured placement strategy. Activation rebalancing can improve distribution, but it doesn't remove hot keys or replace capacity planning.
- Use graceful shutdown where possible, but validate abrupt process loss too.

## Observe the application

Use standard .NET logging, metrics, and distributed tracing, and consider the Orleans Dashboard for cluster inspection.

- Include grain type and safe identifiers in logs without recording secrets or sensitive state.
- Monitor call latency, timeouts, rejected requests, queue length, activation counts, memory, CPU, garbage collection, membership changes, and provider health.
- Alert on symptoms visible to users as well as infrastructure signals.
- Keep log levels configurable through the .NET configuration system.
- Use health and readiness checks appropriate to the host environment.

## Test at the right levels

- Unit test domain logic independently where possible.
- Use `Microsoft.Orleans.TestingHost` for tests which need grain activation, serialization, scheduling, or multi-silo behavior.
- Test serialization compatibility for messages and persisted state.
- Add integration tests against the production provider types used by the application.
- Exercise retries, duplicate requests, timeouts, process termination, rolling upgrades, and recovery.
- Load test representative key distributions. Uniform synthetic keys can hide hot-grain problems.

## Review production readiness

Before deployment, confirm that the application has:

- Shared membership and durable providers where required.
- Stable cluster and service identities.
- Authentication and network boundaries.
- Bounded retries and idempotent operations.
- Capacity limits, backpressure, and overload behavior.
- Logs, metrics, traces, health checks, and alerts.
- Backup, restore, upgrade, and rollback procedures.
- Tests for failures which can occur in the chosen infrastructure.

For specific provider packages, see [Orleans NuGet packages](nuget-packages.md). For version upgrades, use the [migration guide](../migration-guide.md).
