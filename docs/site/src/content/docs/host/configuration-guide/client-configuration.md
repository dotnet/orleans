---
title: Client configuration
description: Learn about client configurations in .NET Orleans.
ms.date: 01/21/2026
ms.topic: how-to
zone_pivot_groups: orleans-version
ms.custom: sfi-ropc-nochange
---

# Client configuration

:::zone target="docs" pivot="orleans-7-0,orleans-8-0,orleans-9-0,orleans-10-0"

Configure a client for connecting to a cluster of silos and sending requests to grains programmatically via an <xref:Microsoft.Extensions.Hosting.IHostBuilder> and several supplemental option classes. Like silo options, client option classes follow the [Options pattern in .NET](../../../core/extensions/options.md).

:::zone-end

:::zone target="docs" pivot="orleans-3-x"

Configure a client for connecting to a cluster of silos and sending requests to grains programmatically via an <xref:Orleans.ClientBuilder> and several supplemental option classes. Like silo options, client option classes follow the [Options pattern in .NET](../../../core/extensions/options.md).

:::zone-end

> [!TIP]
> If you just want to start a local silo and a local client for development purposes, see [Local development configuration](local-development-configuration.md).

:::zone target="docs" pivot="orleans-8-0,orleans-9-0,orleans-10-0"

> [!TIP]
> If you're using [Aspire](../aspire-integration.md), client configuration is handled automatically. Aspire injects <xref:Orleans.Configuration.ClusterOptions.ClusterId>, <xref:Orleans.Configuration.ClusterOptions.ServiceId>, and clustering provider settings via environment variables, so you can use the simpler parameterless <xref:Microsoft.Extensions.Hosting.OrleansClientGenericHostExtensions.UseOrleansClient*> method. See [Orleans and Aspire integration](../aspire-integration.md) for the recommended approach.

:::zone-end

Add the [Microsoft.Orleans.Clustering.AzureStorage](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.AzureStorage) NuGet package to your client project.

There are several key aspects of client configuration:

- Orleans clustering information
- Clustering provider
- Application parts

Example of a client configuration:

:::zone target="docs" pivot="orleans-7-0,orleans-8-0,orleans-9-0,orleans-10-0"

### [Microsoft Entra ID (recommended)](#tab/entra-id)

Using a `TokenCredential` with a service URI is the recommended approach. This pattern avoids storing secrets in configuration and leverages Microsoft Entra ID for secure authentication.

<xref:Azure.Identity.DefaultAzureCredential> provides a credential chain that works seamlessly across local development and production environments. During development, it uses your Azure CLI or Visual Studio credentials. In production on Azure, it automatically uses the managed identity assigned to your resource.

[!INCLUDE [credential-chain-guidance](../../includes/credential-chain-guidance.md)]

```csharp
using Azure.Identity;

var builder = Host.CreateApplicationBuilder(args);
builder.UseOrleansClient(clientBuilder =>
{
    clientBuilder.Configure<ClusterOptions>(options =>
    {
        options.ClusterId = "my-first-cluster";
        options.ServiceId = "MyOrleansService";
    })
    .UseAzureStorageClustering(options =>
    {
        options.ConfigureTableServiceClient(
            new Uri("https://<your-storage-account>.table.core.windows.net"),
            new DefaultAzureCredential());
    });
});

using var host = builder.Build();
await host.StartAsync();
```

### [Connection string](#tab/connection-string)

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.UseOrleansClient(clientBuilder =>
{
    clientBuilder.Configure<ClusterOptions>(options =>
    {
        options.ClusterId = "my-first-cluster";
        options.ServiceId = "MyOrleansService";
    })
    .UseAzureStorageClustering(
        options => options.ConfigureTableServiceClient(
            builder.Configuration["ORLEANS_AZURE_STORAGE_CONNECTION_STRING"]));
});

using var host = builder.Build();
await host.StartAsync();
```

---

:::zone-end

:::zone target="docs" pivot="orleans-3-x"

:::code language="csharp" source="snippets-v3/client-config/Configuration.cs" id="full_client_config":::

:::zone-end

Let's break down the steps used in this sample:

## Orleans clustering information

```csharp
    .Configure<ClusterOptions>(options =>
    {
        options.ClusterId = "orleans-docker";
        options.ServiceId = "AspNetSampleApp";
    })
```

Here, we set two things:

- The <xref:Orleans.Configuration.ClusterOptions.ClusterId?displayProperty=nameWithType> to `"my-first-cluster"`: This is a unique ID for the Orleans cluster. All clients and silos using this ID can directly talk to each other. Some might choose to use a different <xref:Orleans.Configuration.ClusterOptions.ClusterId> for each deployment, for example.
- The <xref:Orleans.Configuration.ClusterOptions.ServiceId?displayProperty=nameWithType> to `"AspNetSampleApp"`: This is a unique ID for your application, used by some providers (e.g., persistence providers). This ID should remain stable across deployments.

## Clustering provider

:::zone target="docs" pivot="orleans-7-0,orleans-8-0,orleans-9-0,orleans-10-0"

### [Microsoft Entra ID (recommended)](#tab/entra-id)

[!INCLUDE [credential-chain-guidance](../../includes/credential-chain-guidance.md)]

```csharp
clientBuilder.UseAzureStorageClustering(options =>
{
    options.ConfigureTableServiceClient(
        new Uri("https://<your-storage-account>.table.core.windows.net"),
        new DefaultAzureCredential());
});
```

### [Connection string](#tab/connection-string)

```csharp
.UseAzureStorageClustering(
    options => options.ConfigureTableServiceClient(connectionString));
```

---

:::zone-end

:::zone target="docs" pivot="orleans-3-x"

:::code language="csharp" source="snippets-v3/client-config/Configuration.cs" id="azure_clustering":::

:::zone-end

The client discovers all available gateways in the cluster using this provider. Several providers are available; here, we use the Azure Table provider.

For more information, see [Server configuration](server-configuration.md).

:::zone target="docs" pivot="orleans-3-x"

## Application parts

:::code language="csharp" source="snippets-v3/client-config/Configuration.cs" id="application_parts":::

For more information, see [Server configuration](server-configuration.md).

:::zone-end
