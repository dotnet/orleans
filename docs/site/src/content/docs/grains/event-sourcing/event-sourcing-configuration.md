---
title: Event sourcing configuration
description: Configure JournaledGrain log consistency and storage in Orleans.
ms.date: 08/02/2026
ms.topic: how-to
---

# Event sourcing configuration

Reference `Microsoft.Orleans.EventSourcing` from the grain implementation project. Grain interface projects don't need that package unless they expose Event Sourcing types in their contracts.

## Register a log-consistency provider

Register one or more providers on the silo:

```csharp
builder.UseOrleans(siloBuilder =>
{
    siloBuilder
        .AddAzureBlobGrainStorage("eventStore", options =>
        {
            options.ConfigureBlobServiceClient(
                builder.Configuration.GetConnectionString("eventStore"));
        })
        .AddStateStorageBasedLogConsistencyProvider("snapshots")
        .AddLogStorageBasedLogConsistencyProvider("shortLogs");
});
```

Available registration methods are:

- <xref:Orleans.Hosting.StateStorageSiloBuilderExtensions.AddStateStorageBasedLogConsistencyProvider*>
- <xref:Orleans.Hosting.LogStorageSiloBuilderExtensions.AddLogStorageBasedLogConsistencyProvider*>
- <xref:Orleans.Hosting.CustomStorageSiloBuilderExtensions.AddCustomStorageBasedLogConsistencyProvider*>

Each also has an `AsDefault` form. If a default log-consistency provider and default grain storage provider are registered, provider attributes can be omitted.

## Select providers on a grain

State storage and log storage use a standard grain storage provider:

```csharp
[LogConsistencyProvider(ProviderName = "snapshots")]
[StorageProvider(ProviderName = "eventStore")]
public sealed class AccountGrain
    : JournaledGrain<AccountState, AccountEvent>, IAccountGrain
{
}
```

The provider names must exactly match registrations on every silo capable of activating the grain.

Custom storage doesn't use <xref:Orleans.Storage.IGrainStorage>. The grain implements <xref:Orleans.EventSourcing.CustomStorage.ICustomStorageInterface`2> and owns the storage operations:

```csharp
[LogConsistencyProvider(ProviderName = "custom")]
public sealed class AccountGrain
    : JournaledGrain<AccountState, AccountEvent>,
      IAccountGrain,
      ICustomStorageInterface<AccountState, AccountEvent>
{
    // Implement ReadStateFromStorage and ApplyUpdatesToStorage.
}
```

## Multi-cluster responsibility

Custom storage owns the write-topology rules needed by a multi-cluster deployment. The `primaryCluster` registration argument is retained by the provider but doesn't restrict submissions, configure Orleans multi-cluster networking, replicate storage, or provide failover. Enforce any single-writer or regional-write rule in the application and storage implementation.
