---
title: Event sourcing overview
description: Learn an overview of event sourcing in .NET Orleans.
ms.date: 05/23/2025
ms.topic: overview
---

# Event sourcing overview

Event sourcing provides a flexible way to manage and persist grain state. An event-sourced grain has many potential advantages over a standard grain. For one, you can use it with many different storage provider configurations, and it supports geo-replication across multiple clusters. Moreover, it cleanly separates the grain class from definitions of the grain state (represented by a grain state object) and grain updates (represented by event objects).

The documentation is structured as follows:

- [JournaledGrain basics](journaledgrain-basics.md) explains how to define event-sourced grains by deriving from <xref:Orleans.EventSourcing.JournaledGrain`2>, how to access the current state, and how to raise events that update the state.

- [Replicated instances](replicated-instances.md) explains how the event-sourcing mechanism handles replicated grain instances and ensures consistency. It discusses the possibility of racing events and conflicts, and how to address them.

- [Immediate/Delayed confirmation](immediate-vs-delayed-confirmation.md) explains how delayed confirmation of events and reentrancy can improve availability and throughput.

- [Notifications](notifications.md) explains how to subscribe to notifications, allowing grains to react to new events.

- [Event sourcing configuration](event-sourcing-configuration.md) explains how to configure projects, clusters, and log-consistency providers.

- [Built-in log-consistency providers](log-consistency-providers.md) explains how the three currently included log-consistency providers work.

- [JournaledGrain diagnostics](journaledgrain-diagnostics.md) explains how to monitor for connection errors and get simple statistics.

The behavior documented above is reasonably stable regarding the `JournaledGrain` API. However, we expect to extend or change the list of log consistency providers soon to more easily allow you to plug in standard event storage systems.
