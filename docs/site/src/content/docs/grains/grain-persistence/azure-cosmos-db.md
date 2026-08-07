---
title: Azure Cosmos DB grain persistence
description: Configure Azure Cosmos DB for NoSQL as an Orleans grain storage provider.
ms.date: 08/02/2026
ms.topic: how-to
---

# Azure Cosmos DB for NoSQL grain persistence

The [`Microsoft.Orleans.Persistence.Cosmos`](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.Cosmos) package stores grain state as items in [Azure Cosmos DB for NoSQL](https://learn.microsoft.com/azure/cosmos-db/overview). Clustering is configured separately using [`Microsoft.Orleans.Clustering.Cosmos`](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.Cosmos); installing or configuring clustering isn't required merely to use Cosmos DB for grain state.

## Configure storage

Configure a named provider with <xref:Orleans.Hosting.HostingExtensions.AddCosmosGrainStorage*>. Microsoft Entra authentication avoids storing account keys:

```csharp
using Azure.Identity;

siloBuilder.AddCosmosGrainStorage(
    "profileStore",
    options =>
    {
        options.ConfigureCosmosClient(
            "https://account.documents.azure.com:443/",
            new DefaultAzureCredential());
        options.DatabaseName = "Orleans";
        options.ContainerName = "OrleansStorage";
        options.IsResourceCreationEnabled = false;
    });
```

<xref:Orleans.Persistence.Cosmos.CosmosGrainStorageOptions.DatabaseName> defaults to `Orleans`, <xref:Orleans.Persistence.Cosmos.CosmosGrainStorageOptions.ContainerName> defaults to `OrleansStorage`, and <xref:Orleans.Persistence.Cosmos.CosmosGrainStorageOptions.PartitionKeyPath> defaults to `/PartitionKey`. Provision the database and container before startup when <xref:Orleans.Persistence.Cosmos.CosmosGrainStorageOptions.IsResourceCreationEnabled> is `false`. Enabling resource creation is convenient for development but production provisioning is usually managed separately.

## Partitioning and indexing

The <xref:Orleans.Persistence.Cosmos.DefaultDocumentIdProvider> derives the document ID and partition key for each grain record. To customize either value, implement <xref:Orleans.Persistence.Cosmos.IDocumentIdProvider> and register it using the generic <xref:Orleans.Hosting.HostingExtensions.AddCosmosGrainStorage*?displayProperty=nameWithType> overload.

Changing the partition-key path or partition-key algorithm after data exists is a data migration. All silos using the same provider name must agree on the database, container, partition-key path, and provider implementation.

Use <xref:Orleans.Persistence.Cosmos.CosmosGrainStorageOptions.StateFieldsToIndex> to opt selected serialized state fields into indexing. Index only fields used by operational queries: additional indexing increases write cost. Grain storage itself retrieves records by identity and doesn't provide a general query API.

## Concurrency and deployment

The provider maps Cosmos DB ETags to Orleans ETags. Concurrent writes from stale activations fail with <xref:Orleans.Storage.InconsistentStateException>.

When evolving state:

- Keep the configured serializer able to read existing items.
- Deploy compatible readers before writing a new representation.
- Treat container changes, partition-key changes, and serializer changes as explicit migrations.
