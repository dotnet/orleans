---
title: Azure Storage grain persistence
description: Learn about Azure Storage grain persistence in .NET Orleans.
ms.date: 01/21/2026
ms.topic: how-to
zone_pivot_groups: orleans-version
---

# Azure Storage grain persistence

The Azure Storage grain persistence provider supports both [Azure Blob Storage](/azure/storage/blobs/storage-blobs-introduction) and [Azure Table Storage](/azure/storage/common/storage-introduction?toc=/azure/storage/blobs/toc.json#table-storage).

:::zone target="docs" pivot="orleans-7-0,orleans-8-0,orleans-9-0,orleans-10-0"

## Configure Azure Table Storage

Install the [Microsoft.Orleans.Persistence.AzureStorage](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.AzureStorage) package from NuGet. The Azure Table Storage provider stores state in a table row, splitting the state across multiple columns if it exceeds the limits of a single column. Each row can hold a maximum of 1 megabyte, as [imposed by Azure Table Storage](/azure/storage/common/storage-scalability-targets#azure-table-storage-scale-targets).

Configure the Azure Table Storage grain persistence provider using the <xref:Orleans.Hosting.AzureTableSiloBuilderExtensions.AddAzureTableGrainStorage*?displayProperty=nameWithType> extension method.

### [Microsoft Entra ID (recommended)](#tab/entra-id)

Using a `TokenCredential` with a service URI is the recommended approach. This pattern avoids storing secrets in configuration and leverages Microsoft Entra ID for secure authentication.

<xref:Azure.Identity.DefaultAzureCredential> provides a credential chain that works seamlessly across local development and production environments. During development, it uses your Azure CLI or Visual Studio credentials. In production on Azure, it automatically uses the managed identity assigned to your resource.

[!INCLUDE [credential-chain-guidance](../../includes/credential-chain-guidance.md)]

```csharp
using Azure.Identity;

siloBuilder.AddAzureTableGrainStorage(
    name: "profileStore",
    configureOptions: options =>
    {
        options.ConfigureTableServiceClient(
            new Uri("https://<your-storage-account>.table.core.windows.net"),
            new DefaultAzureCredential());
    });
```

### [Connection string](#tab/connection-string)

> [!WARNING]
> Connection strings contain secrets and should be avoided in production. Use Microsoft Entra ID authentication whenever possible.

```csharp
siloBuilder.AddAzureTableGrainStorage(
    name: "profileStore",
    configureOptions: options =>
    {
        options.ConfigureTableServiceClient(
            "DefaultEndpointsProtocol=https;AccountName=data1;AccountKey=SOMETHING1");
    });
```

---

## Configure Azure Blob Storage

The Azure Blob Storage provider stores state in a blob.

Configure the Azure Blob Storage grain persistence provider using the <xref:Orleans.Hosting.AzureBlobSiloBuilderExtensions.AddAzureBlobGrainStorage*?displayProperty=nameWithType> extension method.

### [Microsoft Entra ID (recommended)](#tab/entra-id)

[!INCLUDE [credential-chain-guidance](../../includes/credential-chain-guidance.md)]

```csharp
using Azure.Identity;

siloBuilder.AddAzureBlobGrainStorage(
    name: "profileStore",
    configureOptions: options =>
    {
        options.ConfigureBlobServiceClient(
            new Uri("https://<your-storage-account>.blob.core.windows.net"),
            new DefaultAzureCredential());
    });
```

### [Connection string](#tab/connection-string)

```csharp
siloBuilder.AddAzureBlobGrainStorage(
    name: "profileStore",
    configureOptions: options =>
    {
        options.ConfigureBlobServiceClient(
             "DefaultEndpointsProtocol=https;AccountName=data1;AccountKey=SOMETHING1");
    });
```

---

:::zone-end

:::zone target="docs" pivot="orleans-8-0,orleans-9-0,orleans-10-0"

## Aspire integration for grain persistence

[Aspire](../../host/aspire-integration.md) simplifies Azure Storage grain persistence configuration by managing resource provisioning and connection automatically.

### Azure Blob Storage with Aspire

**AppHost project (Program.cs):**

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var storage = builder.AddAzureStorage("storage");
var blobs = storage.AddBlobs("grainstate");

var orleans = builder.AddOrleans("cluster")
    .WithClustering(builder.AddRedis("redis"))
    .WithGrainStorage("Default", blobs);

builder.AddProject<Projects.MySilo>("silo")
    .WithReference(orleans)
    .WithReference(blobs);

builder.Build().Run();
```

**Silo project (Program.cs):**

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddKeyedAzureBlobServiceClient("grainstate");
builder.UseOrleans();

builder.Build().Run();
```

### Azure Table Storage with Aspire

**AppHost project (Program.cs):**

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var storage = builder.AddAzureStorage("storage");
var tables = storage.AddTables("grainstate");

var orleans = builder.AddOrleans("cluster")
    .WithClustering(builder.AddRedis("redis"))
    .WithGrainStorage("Default", tables);

builder.AddProject<Projects.MySilo>("silo")
    .WithReference(orleans)
    .WithReference(tables);

builder.Build().Run();
```

**Silo project (Program.cs):**

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddKeyedAzureTableServiceClient("grainstate");
builder.UseOrleans();

builder.Build().Run();
```

> [!TIP]
> During local development, Aspire automatically uses the Azurite emulator for Azure Storage. In production deployments, Aspire connects to your real Azure Storage account based on your Azure deployment configuration.

> [!IMPORTANT]
> You must call the appropriate `AddKeyed*` method (such as `AddKeyedAzureBlobServiceClient` or `AddKeyedAzureTableServiceClient`) to register the storage client in the dependency injection container. Orleans providers look up resources by their keyed service name—if you skip this step, Orleans won't be able to resolve the storage client and will throw a dependency resolution error at runtime.

For comprehensive documentation on Orleans and Aspire integration, see [Orleans and Aspire integration](../../host/aspire-integration.md).

:::zone-end

:::zone target="docs" pivot="orleans-3-x"

## Configure Azure Table Storage

Install the [Microsoft.Orleans.Persistence.AzureStorage](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.AzureStorage) package from NuGet. The Azure Table Storage provider stores state in a table row, splitting the state across multiple columns if it exceeds the limits of a single column. Each row can hold a maximum of 1 megabyte, as [imposed by Azure Table Storage](/azure/storage/common/storage-scalability-targets#azure-table-storage-scale-targets).

Configure the Azure Table Storage grain persistence provider using the <xref:Orleans.Hosting.AzureTableSiloBuilderExtensions.AddAzureTableGrainStorage*?displayProperty=nameWithType> extension method.

```csharp
siloBuilder.AddAzureTableGrainStorage(
    name: "profileStore",
    configureOptions: options =>
    {
        options.ConnectionString =
            "DefaultEndpointsProtocol=https;AccountName=data1;AccountKey=SOMETHING1";
    });
```

## Configure Azure Blob Storage

The Azure Blob Storage provider stores state in a blob.

Configure the Azure Blob Storage grain persistence provider using the <xref:Orleans.Hosting.AzureBlobSiloBuilderExtensions.AddAzureBlobGrainStorage*?displayProperty=nameWithType> extension method.

```csharp
siloBuilder.AddAzureBlobGrainStorage(
    name: "profileStore",
    configureOptions: options =>
    {
        options.ConnectionString =
             "DefaultEndpointsProtocol=https;AccountName=data1;AccountKey=SOMETHING1";
    });
```

:::zone-end
