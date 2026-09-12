---
title: Event sourcing configuration
description: Configure JournaledGrain log consistency and storage in Orleans.
ms.date: 08/23/2026
ms.topic: how-to
---

# Event sourcing configuration

Reference `Microsoft.Orleans.EventSourcing` from the grain implementation project. Grain interface projects don't need that package unless they expose Event Sourcing types in their contracts.

## Register a log-consistency provider

Register one or more providers on the silo:

:::code language="csharp" source="../../snippets/compiled/EventSourcing/EventSourcingSnippets.cs" id="register_log_consistency":::

Available registration methods are:

- <xref:Orleans.Hosting.StateStorageSiloBuilderExtensions.AddStateStorageBasedLogConsistencyProvider*>
- <xref:Orleans.Hosting.LogStorageSiloBuilderExtensions.AddLogStorageBasedLogConsistencyProvider*>
- <xref:Orleans.Hosting.CustomStorageSiloBuilderExtensions.AddCustomStorageBasedLogConsistencyProvider*>
- <xref:Orleans.Hosting.JournaledStateSiloBuilderExtensions.AddJournaledStateBasedLogConsistencyProvider*>

Each also has an `AsDefault` form. If a default log-consistency provider and default grain storage provider are registered, provider attributes can be omitted.

## Select providers on a grain

State storage and log storage use a standard grain storage provider:

:::code language="csharp" source="../../snippets/compiled/EventSourcing/EventSourcingSnippets.cs" id="select_log_consistency_provider":::

The provider names must exactly match registrations on every silo capable of activating the grain.

Custom storage doesn't use <xref:Orleans.Storage.IGrainStorage>. The grain implements <xref:Orleans.EventSourcing.CustomStorage.ICustomStorageInterface`2> and owns the storage operations:

:::code language="csharp" source="../../snippets/compiled/EventSourcing/EventSourcingSnippets.cs" id="custom_storage_grain":::

## Journaled-state provider

The journaled-state provider stores the event log in the same Orleans journal as the activation's other durable states. One write atomically publishes the captured event log, write marker, and auxiliary durable state. Snapshot replacement failures leave the previous journal generation published and retain the captured changes for retry.

This provider runs on a single turn-serialized grain activation. Grain types configured for reentrancy, selective interleaving, always-interleaved methods, or stateless-worker placement are rejected during activation.

The provider's persisted write marker supports a versioned length-prefixed encoding, so every valid `ClusterId`, including identifiers containing commas and punctuation, has an exact identity. Existing comma-token markers remain readable and continue using that representation while all cluster identifiers are delimiter-safe, preserving rolling upgrades with previous Orleans versions.

Complete the Orleans rolling upgrade before configuring a comma-containing `ClusterId`. The first write from that cluster upgrades the marker to the versioned representation, which previous Orleans versions don't understand. After that upgrade, rollback requires restoring a journal generation written with the legacy marker format.

## Multi-cluster responsibility

Custom storage owns the write-topology rules needed by a multi-cluster deployment. The `primaryCluster` registration argument is retained by the provider but doesn't restrict submissions, configure Orleans multi-cluster networking, replicate storage, or provide failover. Enforce any single-writer or regional-write rule in the application and storage implementation.
