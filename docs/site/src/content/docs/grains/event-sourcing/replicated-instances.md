---
title: JournaledGrain instances and conflicts
description: Understand optimistic concurrency and synchronization for JournaledGrain instances.
ms.date: 08/02/2026
ms.topic: concept-article
---

# `JournaledGrain` instances and conflicts

Advanced Orleans deployments can encounter more than one instance representing the same logical journal. Log-consistency providers coordinate those instances around one confirmed event sequence.

At a given confirmed version, instances derive the same state from the same event sequence. Local tentative views can differ while submissions are unconfirmed.

## Racing updates

Unconditional events are eventually ordered by the provider. An event can be confirmed later in the sequence than the tentative view expected, so transition logic must remain valid for any accepted ordering.

When validity depends on the currently observed version, use a conditional event:

```csharp
var accepted = await RaiseConditionalEvent(new Withdrawn(amount));
```

The provider compares the expected confirmed version with storage. If another update advanced the log, it doesn't append the event and returns `false`. The grain can then re-evaluate the command against the refreshed state.

## Explicit synchronization

Call `RefreshNow` to confirm local submissions and refresh from the global log:

```csharp
await RefreshNow();
```

This is useful before a decision that requires the latest confirmed view. It incurs storage/protocol work and can wait while the backing service is unavailable.

## Deployment boundary

The protocol contains cluster-aware notification and concurrency mechanisms, but doesn't itself deliver a geographically replicated application. A multi-cluster deployment must provide:

- Compatible Orleans multi-cluster configuration and connectivity.
- Storage that every participating instance can reach with the required consistency.
- A provider whose behavior fits the topology.
- Application decisions for write regions, failover, latency, and conflict handling.

The custom provider's `primaryCluster` registration argument currently doesn't restrict submissions. Don't rely on it as a write-region, replication, access-control, or failover mechanism.
