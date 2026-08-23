---
title: Log-consistency providers
description: Compare built-in Orleans Event Sourcing log-consistency providers.
ms.date: 08/22/2026
ms.topic: concept-article
---

# Log-consistency providers

`Microsoft.Orleans.EventSourcing` includes three providers:

| Provider | Durable representation | <xref:Orleans.EventSourcing.JournaledGrain`2.RetrieveConfirmedEvents*> | Scale consideration |
|---|---|---|---|
| State storage | Current state snapshot, version, metadata | No | Reads and writes the complete state record |
| Log storage | Complete event sequence and metadata in one record | Yes | Reads and writes the complete event sequence |
| Custom storage | Application-defined | When the implementation exposes event segments | Determined by the implementation |

## State storage

<xref:Orleans.EventSourcing.StateStorage.LogConsistencyProvider> persists a snapshot using a configured <xref:Orleans.Storage.IGrainStorage>. It stores the state, confirmed version, and metadata used to avoid duplication after failures.

Use it when current state is the durable requirement and the complete event history isn't needed. Since every update writes the complete snapshot, large states increase serialization, transfer, and storage cost. Event retrieval isn't available because events aren't retained.

## Log storage

<xref:Orleans.EventSourcing.LogStorage.LogConsistencyProvider> persists the complete event sequence as one object using <xref:Orleans.Storage.IGrainStorage>. It keeps the complete sequence in memory and writes the complete sequence on updates.

It supports <xref:Orleans.EventSourcing.JournaledGrain`2.RetrieveConfirmedEvents*>, but its cost grows with the full history. Use it for samples, tests, or bounded logs. It isn't an append-optimized production event store and isn't suitable for an unbounded event sequence.

## Custom storage

<xref:Orleans.EventSourcing.CustomStorage.LogConsistencyProvider> calls the real
<xref:Orleans.EventSourcing.CustomStorage.ICustomStorageInterface`2> methods
implemented by the grain or returned by a keyed
<xref:Orleans.EventSourcing.CustomStorage.ICustomStorageFactory>:

:::code language="csharp" source="../../snippets/compiled/EventSourcing/EventSourcingSnippets.cs" id="custom_storage_operations":::

<xref:Orleans.EventSourcing.CustomStorage.ICustomStorageInterface`2.ReadStateFromStorage*> returns the confirmed version and state. <xref:Orleans.EventSourcing.CustomStorage.ICustomStorageInterface`2.ApplyUpdatesToStorage*> must atomically compare `expectedVersion` and append/apply the supplied sequence. Return `false` on a version conflict.

<xref:Orleans.EventSourcing.CustomStorage.ICustomStorageInterface`2.ClearStoredState*>
clears the application-owned state when the provider supports destructive log
clearing.

<xref:Orleans.EventSourcing.CustomStorage.ICustomStorageInterface`2.RetrieveLogSegment*>
returns confirmed events for <xref:Orleans.EventSourcing.JournaledGrain`2.RetrieveConfirmedEvents*> when the storage implementation retains them.

The provider retries after exceptions. If storage committed but the response was lost, the same update can be submitted again. The implementation must make retries idempotent or detect duplicate submissions. Returning success before the update is durable violates <xref:Orleans.EventSourcing.JournaledGrain`2.ConfirmEvents*> semantics.

Use custom storage to integrate an append-optimized event store, snapshots plus events, retention, or application-specific migration. The implementation owns durability, concurrency, event retrieval, compaction, and schema evolution.
