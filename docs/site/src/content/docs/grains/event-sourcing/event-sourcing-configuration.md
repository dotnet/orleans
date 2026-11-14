---
title: Event sourcing configuration
description: Configure JournaledGrain log consistency and storage in Orleans.
ms.date: 08/22/2026
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

Each also has an `AsDefault` form. If a default log-consistency provider and default grain storage provider are registered, provider attributes can be omitted.

## Select providers on a grain

State storage and log storage use a standard grain storage provider:

:::code language="csharp" source="../../snippets/compiled/EventSourcing/EventSourcingSnippets.cs" id="select_log_consistency_provider":::

The provider names must exactly match registrations on every silo capable of activating the grain.

Custom storage uses an application-defined <xref:Orleans.EventSourcing.CustomStorage.ICustomStorageInterface`2> instead of <xref:Orleans.Storage.IGrainStorage>. The grain can implement the interface directly. The following grain instead selects the keyed factory registered with the `"custom"` log-consistency provider:

:::code language="csharp" source="../../snippets/compiled/EventSourcing/EventSourcingSnippets.cs" id="custom_storage_grain":::

The generic registration overload creates the keyed <xref:Orleans.EventSourcing.CustomStorage.ICustomStorageFactory> registration. Its provider name is the key used by <xref:Orleans.Providers.LogConsistencyProviderAttribute>:

:::code language="csharp" source="../../snippets/compiled/EventSourcing/EventSourcingSnippets.cs" id="custom_storage_factory":::

The factory receives the activated grain's <xref:Orleans.Runtime.GrainId> and returns the storage instance used for reads, writes, event retrieval, and clearing throughout that activation.

## Multi-cluster responsibility

Custom storage owns the write-topology rules needed by a multi-cluster deployment. The `primaryCluster` registration argument is retained by the provider but doesn't restrict submissions, configure Orleans multi-cluster networking, replicate storage, or provide failover. Enforce any single-writer or regional-write rule in the application and storage implementation.
