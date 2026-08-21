---
title: Orleans Journaling overview
description: Understand the experimental Orleans Journaling programming model for durable grain values and collections.
ms.date: 08/21/2026
ms.topic: overview
---

# Orleans Journaling overview

Orleans Journaling is an experimental persistence model that records mutations to durable values and collections in an ordered per-grain journal. When a grain activates, Orleans replays that journal to reconstruct its in-memory state. A single write can persist changes from multiple durable states owned by the grain.

> [!IMPORTANT]
> `Microsoft.Orleans.Journaling` and its storage-provider packages are pre-release alpha packages. Their APIs carry diagnostic `ORLEANSEXP005`. Evaluate them with an explicit upgrade, rollback, backup, and recovery plan because APIs and storage formats can change before stabilization.

## Programming model

A journaling grain derives from <xref:Orleans.Journaling.DurableGrain> and receives named durable states through keyed dependency injection. Orleans currently provides:

- <xref:Orleans.Journaling.IDurableValue`1>
- <xref:Orleans.Journaling.IDurableDictionary`2>
- <xref:Orleans.Journaling.IDurableList`1>
- <xref:Orleans.Journaling.IDurableQueue`1>
- <xref:Orleans.Journaling.IDurableSet`1>
- <xref:Orleans.Journaling.IDurableTaskCompletionSource`1>
- <xref:Orleans.Runtime.IPersistentState`1> backed by the grain's journal

Mutations update the activation's in-memory state and add encoded operations to its pending journal buffer. Await <xref:Orleans.Journaling.DurableGrain.WriteStateAsync*> at the application durability point. The returned task completes after the storage provider acknowledges the append or snapshot replacement.

Each named state has a stable stream identity within the grain journal. Keep those names stable across deployments so recovery can bind stored operations to the intended state.

## Journal lifecycle

1. During activation setup, Orleans reads the journal in order and replays each state stream.
1. Grain code synchronously mutates durable values and collections during a grain turn.
1. <xref:Orleans.Journaling.DurableGrain.WriteStateAsync*> gathers pending operations for the grain and submits one atomic journal append or replacement to storage.
1. The storage provider can request compaction when its configured size or row threshold is reached. The next write creates a snapshot of the current durable states and atomically replaces the journal.
1. A later activation replays the latest snapshot and subsequent operations to restore the same durable state.

For the detailed guarantees, see [Runtime behavior and consistency](runtime-behavior.md).

## Journal formats

JSON Lines is the default write format. Each line contains a state stream identifier and one encoded operation. Configure source-generated <xref:System.Text.Json.Serialization.JsonSerializerContext> metadata for journaled key, value, and state types when using trimming or Native AOT.

The earlier Orleans binary format remains registered so deployments can read existing data. Providers store the journal format key with the journal. When the configured write format differs from the stored format, recovery uses the stored reader and the next write snapshots the journal in the configured format.

See [Configure Journaling](configuration.md) for format and migration guidance.

## Journaling and Event Sourcing

Orleans offers two separate journal-oriented programming models:

| Model | Application state model | Persistence coordination |
| --- | --- | --- |
| Orleans Journaling | Mutable durable values and collections on <xref:Orleans.Journaling.DurableGrain> | One per-grain journal managed by `Microsoft.Orleans.Journaling` |
| [Orleans Event Sourcing](../event-sourcing/index.md) | Application-defined events applied to `JournaledGrain<TState, TEvent>` | Log-consistency providers confirm, persist, and synchronize events |

Choose Journaling when evaluating operation-based persistence for built-in mutable state structures. Choose Event Sourcing when domain events, event history, and the supported log-consistency programming model are application requirements.

## Articles

- [Use durable state](durable-state.md)
- [Runtime behavior and consistency](runtime-behavior.md)
- [Configure Journaling](configuration.md)
- [Azure Storage providers](azure-storage.md)
- [Redis journal storage](redis-journal-storage.md)
- [Operate and troubleshoot Journaling](operations.md)
- [Run the Journaling sample](samples.md)
