---
title: Azure Cosmos DB grain persistence
description: Use the Azure Cosmos DB grain persistence provider to persist Azure Cosmos DB for NoSQL data in a .NET Orleans application.
ms.topic: how-to
ms.devlang: csharp
ms.date: 05/23/2025
---

# Azure Cosmos DB for NoSQL grain persistence

The Azure Cosmos DB grain persistence provider supports the [API for NoSQL](/azure/cosmos-db/nosql).

## Install NuGet package

Install the [Microsoft.Orleans.Persistence.Cosmos](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.Cosmos) and [Microsoft.Orleans.Clustering.Cosmos](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.Cosmos) NuGet packages. The Azure Cosmos DB provider stores state in a container item.

> [!IMPORTANT]
> The default database name used by the provider is **Orleans**. The default clustering container name is **OrleansCluster** and the default storage container name is **OrleansStorage**. The cluster container expects a partition key value of `/ClusterId` and the storage container expects `/PartitionKey`.

## Configure clustering provider

To configure the clustering provider, use the `HostingExtensions.UseCosmosClustering` extension method. In this method, you can customize the name and throughput of the database or container, enable resource creation, or configure the client's credentials.

```csharp
siloBuilder.UseCosmosClustering(
    configureOptions: static options =>
    {
        options.IsResourceCreationEnabled = true;
        options.DatabaseName = "OrleansAlternativeDatabase";
        options.ContainerName = "OrleansClusterAlternativeContainer";
        options.ContainerThroughputProperties = ThroughputProperties.CreateAutoscaleThroughput(1000);
        options.ConfigureCosmosClient("<azure-cosmos-db-nosql-connection-string>");
    });
```

## Configure storage provider

Configure the Azure Cosmos DB grain persistence provider using the `HostingExtensions.AddCosmosGrainStorage` extension method.

```csharp
siloBuilder.AddCosmosGrainStorage(
    name: "profileStore",
    configureOptions: static options =>
    {
        options.IsResourceCreationEnabled = true;
        options.DatabaseName = "OrleansAlternativeDatabase";
        options.ContainerName = "OrleansStorageAlternativeContainer";
        options.ContainerThroughputProperties = ThroughputProperties.CreateAutoscaleThroughput(1000);
        options.ConfigureCosmosClient("<azure-cosmos-db-nosql-connection-string>");
    });
```
