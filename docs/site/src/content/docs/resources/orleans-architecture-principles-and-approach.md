---
title: Orleans architecture design principles
description: Understand the design principles behind Orleans 10.
ms.date: 08/02/2026
ms.topic: conceptual
---

# Orleans architecture design principles

Orleans is designed to help .NET developers build stateful distributed applications without making every application implement identity, placement, lifecycle, messaging, and membership from first principles.

## Familiar programming model

Orleans represents distributed entities using .NET interfaces and classes. Calls are asynchronous to make the network boundary explicit, while dependency injection, hosting, configuration, logging, and testing use standard .NET patterns.

Familiarity doesn't make a grain call equivalent to a local method call. Serialization, latency, partial failure, cancellation, and retries remain part of API design.

## Identity independent of activation

A grain's logical identity is stable even when no activation is in memory. Orleans can activate it on demand, place it on a silo, route calls to it, and remove the activation when idle.

This indirection is the foundation for location transparency and resource management. It also means applications should avoid encoding process locations in domain contracts.

## Isolation and partitioning

Grains encapsulate state and process turns one at a time by default. This favors designs made of many independently addressable entities and reduces shared-memory coordination.

Orleans provides scalable building blocks, not automatic scalability for every model. Grain boundaries, key distribution, call patterns, external dependencies, and storage choices determine how an application scales.

## Recovery-oriented operation

Processes and networks fail. Orleans detects membership changes and can reactivate grains on healthy silos after failures. In-flight calls can still fail, and volatile state is lost with its process.

Applications therefore need explicit durability, idempotency, bounded retries, observability, and operational procedures. The runtime handles common mechanics but doesn't invent application-level recovery semantics.

## Extensible providers

Clustering, persistence, reminders, streams, durable jobs, serialization, and related services expose provider models. Applications can select infrastructure which fits their environment or implement a custom provider.

The provider boundary keeps the programming model portable, but each backend retains its own limits, consistency, availability, authentication, and operational characteristics.

## Make the safe path straightforward

Orleans APIs aim to guide applications toward asynchronous execution, isolated state, stable serialization contracts, and managed lifecycle. Defaults target broadly useful behavior while leaving advanced placement, concurrency, and provider customization available when requirements justify them.

The design priority is a coherent foundation for common stateful distributed applications, not a universal abstraction for every distributed workload.
