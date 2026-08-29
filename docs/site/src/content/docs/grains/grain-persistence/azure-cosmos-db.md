---
title: Azure Cosmos DB grain persistence
description: Configure Azure Cosmos DB for NoSQL as an Orleans grain storage provider.
ms.date: 08/29/2026
ms.topic: how-to
---

# Azure Cosmos DB for NoSQL grain persistence

The [`Microsoft.Orleans.Persistence.Cosmos`](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.Cosmos) package stores grain state as items in [Azure Cosmos DB for NoSQL](https://learn.microsoft.com/azure/cosmos-db/overview). Clustering is configured separately using [`Microsoft.Orleans.Clustering.Cosmos`](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.Cosmos); installing or configuring clustering isn't required merely to use Cosmos DB for grain state.

## Configure storage

Configure a named provider with <xref:Orleans.Hosting.HostingExtensions.AddCosmosGrainStorage*>. Microsoft Entra authentication avoids storing account keys:

:::code language="csharp" source="../../snippets/compiled/Grains/PersistenceSnippets.cs" id="azure_identity_using_cosmos":::

:::code language="csharp" source="../../snippets/compiled/Grains/PersistenceSnippets.cs" id="configure_cosmos_storage":::
<xref:Orleans.Persistence.Cosmos.CosmosOptions.DatabaseName> defaults to `Orleans`, <xref:Orleans.Persistence.Cosmos.CosmosOptions.ContainerName> defaults to `OrleansStorage`, and <xref:Orleans.Persistence.Cosmos.CosmosGrainStorageOptions.PartitionKeyPath> defaults to `/PartitionKey`. Provision the database and container before startup when <xref:Orleans.Persistence.Cosmos.CosmosOptions.IsResourceCreationEnabled> is `false`. Enabling resource creation is convenient for development but production provisioning is usually managed separately.

## Partitioning and indexing

The <xref:Orleans.Persistence.Cosmos.DefaultDocumentIdProvider> derives the document ID and partition key for each grain record. To customize either value, implement <xref:Orleans.Persistence.Cosmos.IDocumentIdProvider> and register it using the generic <xref:Orleans.Hosting.HostingExtensions.AddCosmosGrainStorage*?displayProperty=nameWithType> overload.

Single-string partitioning remains the default. It uses <xref:Orleans.Persistence.Cosmos.CosmosGrainStorageOptions.PartitionKeyPath>, which defaults to `/PartitionKey`. Existing providers, containers, and documents continue to use this behavior without configuration changes.

Set <xref:Orleans.Persistence.Cosmos.CosmosGrainStorageOptions.PartitionKeyLevelCount> to `2` or `3` to opt into hierarchical partition keys. Orleans uses these ordered paths:

1. `/PartitionKey`
1. `/PartitionKey2`
1. `/PartitionKey3`

The configured level count selects the first two or all three paths. An HPK-aware <xref:Orleans.Persistence.Cosmos.IDocumentIdProvider> returns the same number of values, in the same order, using <xref:Orleans.Persistence.Cosmos.IDocumentIdProvider.GetDocumentKey*>:

```csharp
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Persistence.Cosmos;
using Orleans.Runtime;

public sealed class TenantDocumentIdProvider(IOptions<ClusterOptions> clusterOptions) : IDocumentIdProvider
{
    private readonly DefaultDocumentIdProvider _defaultProvider = new(clusterOptions);

    public ValueTask<(string DocumentId, string PartitionKey)> GetDocumentIdentifiers(
        string grainType,
        GrainId grainId)
    {
        var tenantId = grainId.Key.ToString()!;
        return new((_defaultProvider.GetId(grainType, grainId), tenantId));
    }

    public ValueTask<CosmosDocumentKey> GetDocumentKey(string grainType, GrainId grainId)
    {
        var tenantId = grainId.Key.ToString()!;
        return new(new CosmosDocumentKey(
            _defaultProvider.GetId(grainType, grainId),
            [tenantId, grainType]));
    }
}
```

Register the provider and select the matching depth:

```csharp
siloBuilder.AddCosmosGrainStorage<TenantDocumentIdProvider>(
    "cosmosStore",
    options =>
    {
        options.ConfigureCosmosClient(connectionString);
        options.PartitionKeyLevelCount = 2;
    });
```

At startup, Orleans reads the container definition and compares its partition-key paths with the provider configuration. Startup fails if the mode, path count, path names, or ordering differ. This check also runs when resource creation is disabled. A provider that returns the wrong number of values fails with an <xref:OrleansConfigurationException> before the grain operation reaches Cosmos DB.

Cosmos DB cannot change an existing container's partition-key definition in place. Moving from single-string partitioning to HPK requires a new container and a data copy or [container copy job](https://learn.microsoft.com/azure/cosmos-db/hierarchical-partition-keys-faq#can-i-add-hierarchical-partition-keys-to-existing-containers). Orleans does not migrate the data automatically. All silos using the same provider name must agree on the database, container, partition-key configuration, and provider implementation.

Use <xref:Orleans.Persistence.Cosmos.CosmosGrainStorageOptions.StateFieldsToIndex> to opt selected serialized state fields into indexing. Index only fields used by operational queries: additional indexing increases write cost. Grain storage itself retrieves records by identity and doesn't provide a general query API.

## Concurrency and deployment

The provider maps Cosmos DB ETags to Orleans ETags. Concurrent writes from stale activations fail with <xref:Orleans.Storage.InconsistentStateException>.

When evolving state:

- Keep the configured serializer able to read existing items.
- Deploy compatible readers before writing a new representation.
- Treat container changes, partition-key changes, and serializer changes as explicit migrations.
