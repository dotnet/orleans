---
title: Server configuration
description: Learn how to configure .NET Orleans server settings.
ms.date: 01/21/2026
ms.topic: how-to
zone_pivot_groups: orleans-version
ms.custom: sfi-ropc-nochange
---

# Server configuration

:::zone target="docs" pivot="orleans-7-0,orleans-8-0,orleans-9-0,orleans-10-0"

Configure a silo programmatically using the <xref:Microsoft.Extensions.Hosting.GenericHostExtensions.UseOrleans(Microsoft.Extensions.Hosting.IHostBuilder,System.Action{Microsoft.Extensions.Hosting.HostBuilderContext,Orleans.Hosting.ISiloBuilder})> extension method and several supplemental option classes. Option classes in Orleans follow the [Options pattern in .NET](../../../core/extensions/options.md) and can be loaded from files, environment variables, or any other valid configuration provider.

There are several key aspects of silo configuration:

- Clustering provider
- (Optional) Orleans clustering information
- (Optional) Endpoints for silo-to-silo and client-to-silo communications

> [!TIP]
> If you're using [Aspire](../aspire-integration.md) (Orleans 8.0+), most of this configuration is handled automatically. Aspire injects <xref:Orleans.Configuration.ClusterOptions.ClusterId>, <xref:Orleans.Configuration.ClusterOptions.ServiceId>, and endpoint configuration via environment variables, so you can use the simpler parameterless <xref:Microsoft.Extensions.Hosting.GenericHostExtensions.UseOrleans*> method. See [Orleans and Aspire integration](../aspire-integration.md) for the recommended approach.

This example shows a silo configuration defining cluster information and using Azure Table Storage for clustering:

### [Microsoft Entra ID (recommended)](#tab/entra-id)

Using a `TokenCredential` with a service URI is the recommended approach. This pattern avoids storing secrets in configuration and leverages Microsoft Entra ID for secure authentication.

<xref:Azure.Identity.DefaultAzureCredential> provides a credential chain that works seamlessly across local development and production environments. During development, it uses your Azure CLI or Visual Studio credentials. In production on Azure, it automatically uses the managed identity assigned to your resource.

[!INCLUDE [credential-chain-guidance](../../includes/credential-chain-guidance.md)]

```csharp
using Azure.Identity;

using IHost host = Host.CreateDefaultBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder.UseAzureStorageClustering(options =>
        {
            options.ConfigureTableServiceClient(
                new Uri("https://<your-storage-account>.table.core.windows.net"),
                new DefaultAzureCredential());
        });
    })
    .UseConsoleLifetime()
    .Build();
```

### [Connection string](#tab/connection-string)

> [!WARNING]
> Connection strings contain secrets and should be avoided in production. Use Microsoft Entra ID authentication whenever possible.

```csharp
using IHost host = Host.CreateDefaultBuilder(args)
    .UseOrleans(builder =>
    {
        builder.UseAzureStorageClustering(
            options => options.ConfigureTableServiceClient(connectionString));
    })
    .UseConsoleLifetime()
    .Build();
```

---

> [!TIP]
> When developing for Orleans, you can call <xref:Orleans.Hosting.CoreHostingExtensions.UseLocalhostClustering(Orleans.Hosting.ISiloBuilder,System.Int32,System.Int32,System.Net.IPEndPoint,System.String,System.String)> to configure a local cluster. In production environments, you should use a clustering provider that is suitable for your deployment.

## Clustering provider

### [Microsoft Entra ID (recommended)](#tab/entra-id)

[!INCLUDE [credential-chain-guidance](../../includes/credential-chain-guidance.md)]

```csharp
siloBuilder.UseAzureStorageClustering(options =>
{
    options.ConfigureTableServiceClient(
        new Uri("https://<your-storage-account>.table.core.windows.net"),
        new DefaultAzureCredential());
});
```

### [Connection string](#tab/connection-string)

```csharp
siloBuilder.UseAzureStorageClustering(
    options => options.ConfigureTableServiceClient(connectionString))
```

---

Usually, you deploy a service built on Orleans on a cluster of nodes, either on dedicated hardware or in the cloud. For development and basic testing, you can deploy Orleans in a single-node configuration. When deployed to a cluster of nodes, Orleans internally implements protocols to discover and maintain membership of Orleans silos in the cluster, including detecting node failures and automatic reconfiguration.

For reliable cluster membership management, Orleans uses Azure Table, SQL Server, or Apache ZooKeeper for node synchronization.

In this sample, we use Azure Table as the membership provider.

## Orleans clustering information

To optionally configure clustering, use <xref:Orleans.Configuration.ClusterOptions> as the type parameter for the <xref:Orleans.Hosting.SiloBuilderExtensions.Configure*> method on the <xref:Orleans.Hosting.ISiloBuilder> instance.

```csharp
siloBuilder.Configure<ClusterOptions>(options =>
{
    options.ClusterId = "my-first-cluster";
    options.ServiceId = "SampleApp";
})
```

Here, you specify two options:

- Set the <xref:Orleans.Configuration.ClusterOptions.ClusterId> to `"my-first-cluster"`: This is a unique ID for the Orleans cluster. All clients and silos using this ID can talk directly to each other. You can choose to use a different <xref:Orleans.Configuration.ClusterOptions.ClusterId> for different deployments, though.
- Set the <xref:Orleans.Configuration.ClusterOptions.ServiceId> to `"SampleApp"`: This is a unique ID for your application used by some providers, such as persistence providers. **This ID should remain stable and not change across deployments**.

By default, Orleans uses `"default"` for both <xref:Orleans.Configuration.ClusterOptions.ServiceId> and <xref:Orleans.Configuration.ClusterOptions.ClusterId>. These values don't need changing in most cases. <xref:Orleans.Configuration.ClusterOptions.ServiceId> is more significant and distinguishes different logical services, allowing them to share backend storage systems without interference. <xref:Orleans.Configuration.ClusterOptions.ClusterId> determines which hosts connect to form a cluster.

Within each cluster, all hosts must use the same <xref:Orleans.Configuration.ClusterOptions.ServiceId>. However, multiple clusters can share a <xref:Orleans.Configuration.ClusterOptions.ServiceId>. This enables blue/green deployment scenarios where you start a new deployment (cluster) before shutting down another. This is typical for systems hosted in Azure App Service.

The more common case is that <xref:Orleans.Configuration.ClusterOptions.ServiceId> and <xref:Orleans.Configuration.ClusterOptions.ClusterId> remain fixed for the application's lifetime, and you use a rolling deployment strategy. This is typical for systems hosted in Kubernetes and Service Fabric.

## Endpoints

By default, Orleans listens on all interfaces on port `11111` for silo-to-silo communication and port `30000` for client-to-silo communication. To override this behavior, call <xref:Orleans.Hosting.EndpointOptionsExtensions.ConfigureEndpoints(Orleans.Hosting.ISiloBuilder,System.Int32,System.Int32,System.Net.Sockets.AddressFamily,System.Boolean)> and pass in the port numbers you want to use.

```csharp
siloBuilder.ConfigureEndpoints(siloPort: 17_256, gatewayPort: 34_512)
```

In the preceding code:

- The silo port is set to `17_256`.
- The gateway port is set to `34_512`.

An Orleans silo has two typical types of endpoint configuration:

- Silo-to-silo endpoints: Used for communication between silos in the same cluster.
- Client-to-silo (or gateway) endpoints: Used for communication between clients and silos in the same cluster.

This method should suffice in most cases, but you can customize it further if needed. Here's an example of using an external IP address with port forwarding:

```csharp
siloBuilder.Configure<EndpointOptions>(options =>
{
    // Port to use for silo-to-silo
    options.SiloPort = 11_111;
    // Port to use for the gateway
    options.GatewayPort = 30_000;
    // IP Address to advertise in the cluster
    options.AdvertisedIPAddress = IPAddress.Parse("172.16.0.42");
    // The socket used for client-to-silo will bind to this endpoint
    options.GatewayListeningEndpoint = new IPEndPoint(IPAddress.Any, 40_000);
    // The socket used by the gateway will bind to this endpoint
    options.SiloListeningEndpoint = new IPEndPoint(IPAddress.Any, 50_000);
})
```

Internally, the silo listens on `0.0.0.0:40000` and `0.0.0.0:50000`, but the value published in the membership provider is `172.16.0.42:11111` and `172.16.0.42:30000`.

:::zone-end

:::zone target="docs" pivot="orleans-3-x"

Configure a silo programmatically via <xref:Orleans.Hosting.SiloHostBuilder> and several supplemental option classes. Option classes in Orleans follow the [Options pattern in .NET](../../../core/extensions/options.md) and can be loaded from files, environment variables, or any other valid configuration provider.

There are several key aspects of silo configuration:

- Orleans clustering information
- Clustering provider
- Endpoints for silo-to-silo and client-to-silo communications
- Application parts

This example shows a silo configuration defining cluster information, using Azure clustering, and configuring application parts:

:::code language="csharp" source="snippets-v3/server-config/Configuration.cs" id="full_silo_config":::

Let's break down the steps used in this sample:

## Clustering provider

:::code language="csharp" source="snippets-v3/server-config/Configuration.cs" id="azure_clustering":::

Usually, you deploy a service built on Orleans on a cluster of nodes, either on dedicated hardware or in the cloud. For development and basic testing, you can deploy Orleans in a single-node configuration. When deployed to a cluster of nodes, Orleans internally implements protocols to discover and maintain membership of Orleans silos in the cluster, including detecting node failures and automatic reconfiguration.

For reliable cluster membership management, Orleans uses Azure Table, SQL Server, or Apache ZooKeeper for node synchronization.

In this sample, we use Azure Table as the membership provider.

## Orleans clustering information

:::code language="csharp" source="snippets-v3/server-config/Configuration.cs" id="cluster_options":::

Here, we do two things:

- Set the <xref:Orleans.Configuration.ClusterOptions.ClusterId> to `"my-first-cluster"`: This is a unique ID for the Orleans cluster. All clients and silos using this ID can talk directly to each other. You can choose to use a different <xref:Orleans.Configuration.ClusterOptions.ClusterId> for different deployments, though.
- Set the <xref:Orleans.Configuration.ClusterOptions.ServiceId> to `"AspNetSampleApp"`: This is a unique ID for your application used by some providers, such as persistence providers. **This ID should remain stable and not change across deployments**.

By default, Orleans uses `"default"` for both <xref:Orleans.Configuration.ClusterOptions.ServiceId> and <xref:Orleans.Configuration.ClusterOptions.ClusterId>. These values don't need changing in most cases. <xref:Orleans.Configuration.ClusterOptions.ServiceId> is more significant and distinguishes different logical services, allowing them to share backend storage systems without interference. <xref:Orleans.Configuration.ClusterOptions.ClusterId> determines which hosts connect to form a cluster.

Within each cluster, all hosts must use the same <xref:Orleans.Configuration.ClusterOptions.ServiceId>. However, multiple clusters can share a <xref:Orleans.Configuration.ClusterOptions.ServiceId>. This enables blue/green deployment scenarios where you start a new deployment (cluster) before shutting down another. This is typical for systems hosted in Azure App Service.

The more common case is that <xref:Orleans.Configuration.ClusterOptions.ServiceId> and <xref:Orleans.Configuration.ClusterOptions.ClusterId> remain fixed for the application's lifetime, and you use a rolling deployment strategy. This is typical for systems hosted in Kubernetes and Service Fabric.

## Endpoints

:::code language="csharp" source="snippets-v3/server-config/Configuration.cs" id="configure_endpoints":::

An Orleans silo has two typical types of endpoint configuration:

- Silo-to-silo endpoints: Used for communication between silos in the same cluster.
- Client-to-silo endpoints (or gateway): Used for communication between clients and silos in the same cluster.

In the sample, we use the helper method `.ConfigureEndpoints(siloPort: 11111, gatewayPort: 30000)`, which sets the port for silo-to-silo communication to `11111` and the gateway port to `30000`. This method detects which interface to listen on.

This method should suffice in most cases, but you can customize it further if needed. Here's an example of using an external IP address with port forwarding:

:::code language="csharp" source="snippets-v3/server-config/Configuration.cs" id="endpoint_options":::

Internally, the silo listens on `0.0.0.0:40000` and `0.0.0.0:50000`, but the value published in the membership provider is `172.16.0.42:11111` and `172.16.0.42:30000`.

## Application parts

:::code language="csharp" source="snippets-v3/server-config/Configuration.cs" id="application_parts":::

Although this step isn't technically required (if not configured, Orleans scans all assemblies in the current folder), we encourage you to configure it. This step helps Orleans load user assemblies and types. These assemblies are referred to as Application Parts. Orleans discovers all Grains, Grain Interfaces, and Serializers using Application Parts.

Configure Application Parts using <xref:Orleans.ApplicationParts.IApplicationPartManager>, accessible via the `ConfigureApplicationParts` extension method on <xref:Orleans.IClientBuilder> and <xref:Orleans.Hosting.ISiloHostBuilder>. The `ConfigureApplicationParts` method accepts a delegate, `Action<IApplicationPartManager>`.

The following extension methods on <xref:Orleans.ApplicationParts.IApplicationPartManager> support common uses:

- <xref:Orleans.ApplicationPartManagerExtensions.AddApplicationPart*>: Add a single assembly using this extension method.
- <xref:Orleans.ApplicationPartManagerExtensions.AddFromAppDomain*>: Adds all assemblies currently loaded in the `AppDomain`.
- <xref:Orleans.ApplicationPartManagerExtensions.AddFromApplicationBaseDirectory*>: Loads and adds all assemblies in the current base path (see <xref:System.AppDomain.BaseDirectory?displayProperty=nameWithType>).

Supplement assemblies added by the above methods using the following extension methods on their return type, <xref:Orleans.ApplicationParts.IApplicationPartManagerWithAssemblies>:

- <xref:Orleans.ApplicationPartManagerExtensions.WithReferences*>: Adds all referenced assemblies from the added parts. This immediately loads any transitively referenced assemblies. Assembly loading errors are ignored.
- <xref:Orleans.Hosting.ApplicationPartManagerCodeGenExtensions.WithCodeGeneration*>: Generates support code for the added parts and adds it to the part manager. Note that this requires installing the `Microsoft.Orleans.OrleansCodeGenerator` package and is commonly referred to as runtime code generation.

Type discovery requires the provided Application Parts to include specific attributes. Adding the build-time code generation package (`Microsoft.Orleans.CodeGenerator.MSBuild` or `Microsoft.Orleans.OrleansCodeGenerator.Build`) to each project containing Grains, Grain Interfaces, or Serializers is the recommended approach to ensure these attributes are present. Build-time code generation only supports C#. For F#, Visual Basic, and other .NET languages, you can generate code during configuration time via the <xref:Orleans.Hosting.ApplicationPartManagerCodeGenExtensions.WithCodeGeneration*> method described above. Find more info regarding code generation in [the corresponding section](../../grains/code-generation.md).

:::zone-end
