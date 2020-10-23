---
layout: page
title: Event Sourcing Overview
---

# Event Sourcing

Event sourcing provides a flexible way to manage and persist grain state. An event-sourced grain has many potential advantages over a standard grain. For one, it can be used with many different storage provider configurations, and supports geo-replication across multiple clusters. Moreover, it cleanly separates the grain class from definitions of the grain state (represented by a grain state object) and grain updates (represented by event objects). 


The documentation is structured as follows:

* [JournaledGrain Basics](journaledgrain_basics.md) explains how to define an event-sourced grains by deriving from `JournaledGrain`, how to access the current state, and how to raise events that update the state.

* [Replicated Instances](replicated_instances.md) explains how the event-sourcing mechanism handles replicated grain instances and ensures consistency. It discusses the possibility of racing events and conflicts, and how to address them.

* [Immediate/Delayed Confirmation](immediate_vs_delayed_confirmation.md) explains how delayed confirmation of events, and reentrancy, can improve availability and throughput.

* [Notifications](notifications.md) explains how to subscribe to notifications, allowing grains to react to new events.

* [Event Sourcing Configuration](event_sourcing_configuration.md) explains how to configure projects, clusters, and log-consistency providers.

* [Built-In Log-Consistency Providers](log_consistency_providers.md) explains how the three currently included log-consistency providers work.

* [JournaledGrain Diagnostics](journaledgrain_diagnostics.md) explains how to monitor for connection errors, and get simple statistics.


The behavior documented above is reasonably stable, as far as the JournaledGrain API is concerned. However, we expect to extend or change the list of log consistency providers soon, to more easily allow developers to plug in  standard event storage systems.

