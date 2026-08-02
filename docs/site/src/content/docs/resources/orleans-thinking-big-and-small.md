---
title: Orleans for systems big and small
description: Understand how Orleans applies from one process to distributed clusters.
ms.date: 08/02/2026
ms.topic: conceptual
---

# Orleans for systems big and small

You don't need a very large deployment to benefit from Orleans. The virtual actor model addresses problems which appear as soon as state and work cross process boundaries: identity, placement, concurrency, messaging, membership, and recovery.

## Start in one process

An Orleans application can host a silo and its callers in one process. This is useful for development and can be appropriate for a small deployment. Grain identity and turn-based execution can still provide useful structure and isolation.

A single process isn't fault tolerant, and localhost or memory providers don't become production infrastructure merely because the application uses Orleans. Choose providers and deployment topology based on the required durability and availability.

## Scale by partitioning

Orleans applications usually partition domain entities across many grain keys. As the cluster grows, Orleans can place activations on available silos and route calls without changing grain references.

Adding silos provides more cluster capacity only when the workload can use it. A single hot grain, shared external dependency, or global coordination point remains a bottleneck. Measure the key distribution and design explicit partitions where needed.

## The same distributed concerns remain

Whether a cluster has two silos or many, applications must account for:

- Calls which can fail, time out, or be retried.
- Membership changes and in-flight work during process failure.
- Durable state, provider availability, and recovery.
- Serialization and contract compatibility.
- Capacity, overload, observability, deployment, and rollback.

Orleans supplies a consistent model and runtime services for these concerns. It doesn't eliminate them or guarantee that an application scales by configuration alone.

## Grow without encoding locations

Grain contracts identify entities by key rather than server. This allows a single-process application and a multi-silo deployment to use the same domain interfaces. Moving to a cluster primarily changes hosting, provider, security, and operations configuration.

That location-independent model is useful at modest scale and remains useful as demand grows. Start with the simplest topology which meets current requirements, retain representative load tests, and scale based on observed bottlenecks.
