---
title: Event sourcing overview
description: Build event-sourced Orleans grains with JournaledGrain and log-consistency providers.
ms.date: 08/02/2026
ms.topic: overview
---

# Event sourcing overview

The supported Orleans Event Sourcing model uses <xref:Orleans.EventSourcing.JournaledGrain`2> from the `Microsoft.Orleans.EventSourcing` package. A journaled grain represents changes as events and derives its current state by applying those events in order.

`JournaledGrain` separates:

- **State**, the aggregate view used to answer requests.
- **Events**, the immutable changes submitted by the grain.
- **Log consistency**, the protocol that orders, confirms, persists, and refreshes events.
- **Storage**, selected by the configured log-consistency provider.

The built-in providers support snapshot storage, a complete event sequence stored as one record, and application-defined storage. Their scalability and retrieval capabilities differ, so select a provider based on the [provider comparison](log-consistency-providers.md).

## Consistency model

The confirmed `Version` is the number of confirmed events. A confirmed state at a given version is derived from one ordered event sequence. Locally raised events can also contribute to `TentativeState` before confirmation.

Log-consistency providers use optimistic concurrency and protocol notifications to coordinate instances that can exist in advanced deployment topologies. This isn't automatic geographic replication: Orleans doesn't provision replicated storage, deploy multiple clusters, route users between regions, or define disaster-recovery policy. Any multi-cluster design must separately provide shared/reachable storage and Orleans multi-cluster connectivity, and must be validated for the selected provider.

## Articles

- [JournaledGrain basics](journaledgrain-basics.md)
- [Replicated instances and conflicts](replicated-instances.md)
- [Immediate and delayed confirmation](immediate-vs-delayed-confirmation.md)
- [Notifications](notifications.md)
- [Event sourcing configuration](event-sourcing-configuration.md)
- [Log-consistency providers](log-consistency-providers.md)
- [JournaledGrain diagnostics](journaledgrain-diagnostics.md)

## Event Sourcing and experimental Journaling

`Microsoft.Orleans.EventSourcing` and `Microsoft.Orleans.Journaling` are separate packages and programming models.

- Event Sourcing uses `JournaledGrain<TState, TEvent>` and log-consistency providers.
- Journaling uses `DurableGrain`, journaled state, and durable collections.

`Microsoft.Orleans.Journaling` is an alpha package whose APIs are marked experimental with diagnostic `ORLEANSEXP005`. It isn't a replacement for Event Sourcing. Evaluate it as an experimental feature and expect API or storage-format changes.
