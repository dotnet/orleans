---
title: Configure PubSub storage
description: Persist Orleans stream subscription metadata with the PubSubStore grain storage provider.
ms.date: 08/02/2026
ms.topic: how-to
---

# Configure PubSub storage

Orleans stream providers use a pub/sub rendezvous to connect producers and consumers. The grain storage provider named `PubSubStore` stores explicit subscription metadata used by the grain-based pub/sub implementation.

`PubSubStore` durability determines whether explicit subscription records survive loss of the cluster's in-memory state:

- `AddMemoryGrainStorage("PubSubStore")` is suitable for development and tests. Records are lost when the cluster state is lost.
- A durable grain storage provider preserves records across silo and cluster restarts, subject to that provider's own availability and consistency.
- Implicit subscriptions are derived from grain metadata instead of being created as explicit subscription records.

Even with durable `PubSubStore`, an explicit consumer must call `ResumeAsync` after activation to attach its current observer instance. Conversely, durable event storage doesn't make subscription records durable. Configure both layers according to the recovery requirement.

## Azure Table Storage example

Managed identity is preferred:

:::code language="csharp" source="snippets/streaming/Configuration.cs" id="pubsub_managed_identity":::

A connection string is also supported:

:::code language="csharp" source="snippets/streaming/Configuration.cs" id="pubsub_connection_string":::

Use a stable Orleans service ID and the same durable storage configuration across cluster restarts. Changing service identity or deleting the backing table creates a logically new subscription registry.

## Operational guidance

- Back up and monitor `PubSubStore` like other application metadata.
- Keep provider names stable. A provider rename changes stream identity from the pub/sub system's perspective.
- Remove subscriptions with `UnsubscribeAsync` when they are no longer needed.
- Before replacing a `PubSubStore`, plan how existing explicit subscriptions will be recreated.

For the internal rendezvous and pulling-agent design, see [Orleans streams implementation](../implementation/streams-implementation/index.md).
