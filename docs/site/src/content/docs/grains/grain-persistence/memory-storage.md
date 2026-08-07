---
title: Memory grain persistence
description: Configure in-memory Orleans grain storage for tests and development.
ms.date: 08/07/2026
ms.topic: how-to
---

# Memory grain persistence

The `Microsoft.Orleans.Persistence.Memory` package stores grain state inside the Orleans cluster. Use it for tests, samples, and disposable local development environments.

> [!WARNING]
> Memory grain storage isn't a production durability mechanism. State doesn't survive loss or restart of the cluster.

## Configure a named provider

Configure a named provider with <xref:Orleans.Hosting.MemoryGrainStorageSiloBuilderExtensions.AddMemoryGrainStorage*>:

:::code language="csharp" source="./snippets/persistence/StorageConfiguration.cs" id="configure_memory":::

The provider name must match the storage name in `[PersistentState]`.

## Configure the default provider

Use <xref:Orleans.Hosting.MemoryGrainStorageSiloBuilderExtensions.AddMemoryGrainStorageAsDefault*> when grains should use memory storage without specifying a storage name:

:::code language="csharp" source="./snippets/persistence/StorageConfiguration.cs" id="configure_memory_default":::

## Storage behavior

<xref:Orleans.Configuration.MemoryGrainStorageOptions.NumStorageGrains> controls how many internal storage grains distribute the records. The default is 10 and the value must be greater than zero.

Memory storage still serializes state through <xref:Orleans.Storage.IGrainStorageSerializer>, so it can expose serialization and schema errors during tests. It doesn't model the latency, availability, capacity, concurrency, or operational behavior of an external production store.
