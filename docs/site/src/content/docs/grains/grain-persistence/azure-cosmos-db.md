---
title: Azure Cosmos DB grain persistence
description: Configure Azure Cosmos DB for NoSQL as an Orleans grain storage provider.
ms.date: 08/02/2026
ms.topic: how-to
---

# Azure Cosmos DB for NoSQL grain persistence

The [`Microsoft.Orleans.Persistence.Cosmos`](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.Cosmos) package stores grain state as items in [Azure Cosmos DB for NoSQL](/azure/cosmos-db/overview). Clustering is configured separately using [`Microsoft.Orleans.Clustering.Cosmos`](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.Cosmos); installing or configuring clustering isn't required merely to use Cosmos DB for grain state.

## Configure storage

Configure a named provider with `AddCosmosGrainStorage`. Microsoft Entra authentication avoids storing account keys:

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

The defaults are database `Orleans`, container `OrleansStorage`, and partition-key path `/PartitionKey`. Provision the database and container before startup when `IsResourceCreationEnabled` is `false`. Enabling resource creation is convenient for development but production provisioning is usually managed separately.

## Partitioning and indexing

The default partition-key provider derives a key for the grain record. If an application needs another partitioning strategy, implement the Cosmos partition-key provider contract and use the generic `AddCosmosGrainStorage<TPartitionKeyProvider>` overload.

Changing the partition-key path or partition-key algorithm after data exists is a data migration. All silos using the same provider name must agree on the database, container, partition-key path, and provider implementation.

Use `StateFieldsToIndex` to opt selected serialized state fields into indexing. Index only fields used by operational queries: additional indexing increases write cost. Grain storage itself retrieves records by identity and doesn't provide a general query API.

## Concurrency and deployment

The provider maps Cosmos DB ETags to Orleans ETags. Concurrent writes from stale activations fail with <xref:Orleans.Storage.InconsistentStateException>.

When evolving state:

- Keep the configured serializer able to read existing items.
- Deploy compatible readers before writing a new representation.
- Treat container changes, partition-key changes, and serializer changes as explicit migrations.
