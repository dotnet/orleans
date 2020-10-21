---
layout: page
title: Azure Storage Grain Persistence
---

# Azure Storage Grain Persistence

The Azure Storage grain persistence provider supports both [Azure Blob Storage](https://azure.microsoft.com/en-us/services/storage/blobs/) and [Azure Table Storage](https://azure.microsoft.com/en-us/services/storage/tables/).

## Installation

Install the [`Microsoft.Orleans.Persistence.AzureStorage`](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.AzureStorage) package from NuGet.

## Configuration

### Azure Table Storage

The Azure Table Storage provider stores state in a table row, splitting the state over multiple columns if the limits of a single column are exceeded. Each row can hold a maximum length of one megabyte, as [imposed by Azure Table Storage](https://docs.microsoft.com/en-us/azure/storage/common/storage-scalability-targets#azure-table-storage-scale-targets).

Configure the Azure Table Storage grain persistence provider using the `ISiloBuilder.AddAzureTableGrainStorage` extension methods.

``` csharp
siloBuilder.AddAzureTableGrainStorage(
    name: "profileStore",
    configureOptions: options =>
    {
        options.UseJson = true;
        options.ConnectionString = "DefaultEndpointsProtocol=https;AccountName=data1;AccountKey=SOMETHING1";
    });
```

### Azure Blob Storage

The Azure Blob Storage provider stores state in a blob.

Configure the Azure Blob Storage grain persistence provider using the `ISiloBuilder.AddAzureBlobGrainStorage` extension methods.

``` csharp
siloBuilder.AddAzureBlobGrainStorage(
    name: "profileStore",
    configureOptions: options =>
    {
        options.UseJson = true;
        options.ConnectionString = "DefaultEndpointsProtocol=https;AccountName=data1;AccountKey=SOMETHING1";
    });
```
