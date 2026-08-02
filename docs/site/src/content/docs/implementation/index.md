---
title: Orleans runtime architecture
description: An advanced guide to the protocols, invariants, and extension points inside the Orleans 10 runtime.
ms.date: 08/02/2026
ms.topic: overview
---

# Orleans runtime architecture

The implementation track explains how Orleans 10 realizes the virtual actor model. It is intended for runtime contributors, provider authors, and operators who need to reason about failure, consistency, scheduling, and extensibility. For application programming guidance, start with the conceptual and task-oriented sections of this documentation instead.

The pages in this track use Orleans source and tests as the specification. Internal types are named so that readers can follow an operation through the repository, but those types are not public compatibility contracts unless the page explicitly identifies an extension point.

## Runtime core

- [Runtime architecture](runtime-architecture.md) follows a call through client, messaging, placement, directory, activation, and scheduling components.
- [Activation lifecycle and migration](activation-lifecycle.md) explains creation, activation, collection, deactivation, and state transfer.
- [Cluster membership](cluster-management.md) describes the failure detector, membership table, ordered views, and death-vote protocol.
- [Grain directory](grain-directory.md) distinguishes the default `LocalGrainDirectory` DHT from the experimental distributed directory.
- [Scheduling and turn execution](scheduler.md) explains `WorkItemGroup`, continuations, interleaving, and single-threaded execution.
- [Messaging and delivery semantics](messaging-delivery-guarantees.md) traces requests and explains why a timeout has an unknown outcome.
- [Placement and activation balancing](load-balancing.md) covers the default resource-optimized policy and the opt-in movement protocols.

## Runtime services and extensibility

- [Lifecycle implementation](orleans-lifecycle.md) describes ordered startup and shutdown.
- [Serialization and code generation](serialization.md) covers generated codecs, proxies, manifests, wire identity, and custom components.
- [Persistent streams](streams-implementation/index.md) explains pulling agents, queue ownership, caches, cursors, pub-sub, and recovery.
- [Provider authoring](provider-authoring.md) describes named providers, configuration binding, lifecycle participation, and validation.
- [TestingHost architecture](testing.md) explains the in-process cluster harness and its substitutions for production services.

## Orleans 10 defaults that shape the architecture

| Concern | Orleans 10 default |
| --- | --- |
| Placement | <xref:Orleans.Runtime.ResourceOptimizedPlacement> |
| Grain directory | `LocalGrainDirectory`, using the membership ring |
| Experimental directory | Opt-in with `AddDistributedGrainDirectory`; warning `ORLEANSEXP003` |
| Experimental directory partitions | `GrainDirectoryOptions.PartitionsPerSilo = 1` |
| Membership probe timeout | 5 seconds |
| Death-vote expiry | 2 minutes |
| Response timeout | 30 seconds, or 30 minutes while a debugger is attached |
| Automatic call retry after response timeout | None |

Configuration values affect failure detection and resource use. This track explains their role in protocols, while the [hosting configuration guide](../host/configuration-guide/index.md) and [deployment guidance](../deployment/index.md) own operational recommendations.
