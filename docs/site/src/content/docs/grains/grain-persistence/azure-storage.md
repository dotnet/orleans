---
title: Azure Storage grain persistence
description: Configure Azure Table Storage or Azure Blob Storage for Orleans grain state.
ms.date: 08/02/2026
ms.topic: how-to
---

# Azure Storage grain persistence

The [`Microsoft.Orleans.Persistence.AzureStorage`](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.AzureStorage) package contains providers for [Azure Table Storage](/azure/storage/tables/table-storage-overview) and [Azure Blob Storage](/azure/storage/blobs/storage-blobs-introduction). Both implement optimistic concurrency using storage ETags.

## Azure Table Storage

Table storage keeps one state record in an entity and splits serialized state across properties when needed. [Azure Table Storage limits an entity to 1 MiB](/azure/storage/tables/table-storage-overview#table-storage-concepts), so use Blob Storage or another provider for larger records.

Configure a named provider with <xref:Orleans.Hosting.AzureTableSiloBuilderExtensions.AddAzureTableGrainStorage*>. Token credentials are preferred over secrets:

```csharp
using Azure.Identity;

siloBuilder.AddAzureTableGrainStorage(
    "profileStore",
    options => options.ConfigureTableServiceClient(
        new Uri("https://account.table.core.windows.net"),
        new DefaultAzureCredential()));
```

Assign the storage account identity the data-plane permissions required to read and write table entities.

## Azure Blob Storage

Blob storage keeps each state record in a blob and is appropriate when state can exceed the Table Storage entity limit:

```csharp
using Azure.Identity;

siloBuilder.AddAzureBlobGrainStorage(
    "cartStore",
    options => options.ConfigureBlobServiceClient(
        new Uri("https://account.blob.core.windows.net"),
        new DefaultAzureCredential()));
```

Assign the storage account identity the data-plane permissions required to read and write blobs.

## Connection strings

Connection strings are useful for local emulators and constrained environments, but contain secrets and shouldn't be committed:

:::code language="csharp" source="./snippets/persistence/StorageConfiguration.cs" id="configure_connection_string":::

Use a secret store when a connection string is unavoidable.

## Operational guidance

- Use separate provider names when records belong in different accounts, containers, or tables.
- Treat an `InconsistentStateException` as an optimistic-concurrency conflict, not a transient timeout.
- Configure storage redundancy and account failover according to the application's durability requirements.
- Test record-size limits using serialized production-shaped state.
