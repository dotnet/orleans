---
title: Azure Storage grain persistence
description: Configure Azure Table Storage or Azure Blob Storage for Orleans grain state.
ms.date: 08/02/2026
ms.topic: how-to
---

# Azure Storage grain persistence

The [`Microsoft.Orleans.Persistence.AzureStorage`](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.AzureStorage) package contains providers for [Azure Table Storage](https://learn.microsoft.com/azure/storage/tables/table-storage-overview) and [Azure Blob Storage](https://learn.microsoft.com/azure/storage/blobs/storage-blobs-introduction). Both implement optimistic concurrency using storage ETags.

## Azure Table Storage

Table storage keeps one state record in an entity and splits serialized state across properties when needed. [Azure Table Storage limits an entity to 1 MiB](https://learn.microsoft.com/azure/storage/tables/table-storage-overview#table-storage-concepts), so use Blob Storage or another provider for larger records.

Configure a named provider with <xref:Orleans.Hosting.AzureTableSiloBuilderExtensions.AddAzureTableGrainStorage*>. Token credentials are preferred over secrets:

:::code language="csharp" source="../../snippets/compiled/Grains/PersistenceSnippets.cs" id="azure_identity_using_table":::

:::code language="csharp" source="../../snippets/compiled/Grains/PersistenceSnippets.cs" id="configure_azure_table_storage":::
Assign the storage account identity the data-plane permissions required to read and write table entities.

## Azure Blob Storage

Blob storage keeps each state record in a blob and is appropriate when state can exceed the Table Storage entity limit:

:::code language="csharp" source="../../snippets/compiled/Grains/PersistenceSnippets.cs" id="azure_identity_using_blob":::

:::code language="csharp" source="../../snippets/compiled/Grains/PersistenceSnippets.cs" id="configure_azure_blob_storage":::
Assign the storage account identity the data-plane permissions required to read and write blobs.

## Connection strings

Connection strings are useful for local emulators and constrained environments, but contain secrets and shouldn't be committed:

:::code language="csharp" source="./snippets/persistence/StorageConfiguration.cs" id="configure_connection_string":::

Use a secret store when a connection string is unavoidable.

## Operational guidance

- Use separate provider names when records belong in different accounts, containers, or tables.
- Treat an <xref:Orleans.Storage.InconsistentStateException> as an optimistic-concurrency conflict, not a transient timeout.
- Configure storage redundancy and account failover according to the application's durability requirements.
- Test record-size limits using serialized production-shaped state.
