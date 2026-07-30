---
title: Local development configuration
description: Learn how to configure .NET Orleans for local development.
ms.date: 05/23/2025
ms.topic: how-to
zone_pivot_groups: orleans-version
---

# Local development configuration

For a working sample application targeting Orleans 7.0, see [Orleans: Hello World](https://github.com/dotnet/samples/tree/main/orleans/HelloWorld). The sample hosts the client and silo in .NET console applications that work on different platforms, while the grains and interfaces target .NET Standard 2.0.

> [!TIP]
> For older versions of Orleans, please see [Orleans sample projects](https://github.com/dotnet/samples/tree/main/orleans).

## Silo configuration

:::zone target="docs" pivot="orleans-7-0,orleans-8-0,orleans-9-0,orleans-10-0"

We recommend using the [Microsoft.Extensions.Hosting](https://www.nuget.org/packages/Microsoft.Extensions.Hosting) NuGet package to configure and run the silo. Also, when developing an Orleans silo, you need the [Microsoft.Orleans.Server](https://www.nuget.org/packages/Microsoft.Orleans.Server) NuGet package. For local Orleans silo development, configure localhost clustering, which uses the loopback address. To use localhost clustering, call the <xref:Orleans.Hosting.CoreHostingExtensions.UseLocalhostClustering*> extension method. Consider this example _Program.cs_ file for the silo host:

```csharp
using Microsoft.Extensions.Hosting;

await Host.CreateDefaultBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder.UseLocalhostClustering();
    })
    .RunConsoleAsync();
```

The preceding code does the following:

- Creates a default host builder.
- Calls the <xref:Microsoft.Extensions.Hosting.GenericHostExtensions.UseOrleans*> extension method to configure the silo.
- Calls the `UseLocalhostClustering` extension method on the given <xref:Orleans.Hosting.ISiloBuilder> to configure the silo to use localhost clustering.
- Chains the `RunConsoleAsync` method to run the silo as a console application.

:::zone-end

:::zone target="docs" pivot="orleans-3-x"

For local development, refer to the example below showing how to configure a silo for this case. It configures and starts a silo listening on the `loopback` address, using `11111` and `30000` as the silo and gateway ports, respectively.

Add the `Microsoft.Orleans.Server` NuGet meta-package to your project.

```dotnetcli
dotnet add package Microsoft.Orleans.Server
```

You need to configure <xref:Orleans.Configuration.ClusterOptions> via <xref:Orleans.Hosting.ISiloBuilder> `Configure` method, specify that you want `LocalhostClustering` as your clustering choice with this silo being the primary, and then configure silo endpoints.

The <xref:Orleans.Hosting.SiloHostBuilderExtensions.ConfigureApplicationParts*> call explicitly adds the assembly containing grain classes to the application setup. It also adds any referenced assembly due to the <xref:Orleans.ApplicationPartManagerExtensions.WithReferences*> extension. After completing these steps, build the silo host and start the silo.

You can create an empty console application project targeting .NET Framework 4.6.1 or higher for hosting a silo.

Here's an example of how you can start a local silo:

:::code language="csharp" source="snippets-v3/local-dev/LocalDevelopment.cs" id="silo_localhost":::

:::zone-end

## Client configuration

:::zone target="docs" pivot="orleans-7-0,orleans-8-0,orleans-9-0,orleans-10-0"

We recommend using the [Microsoft.Extensions.Hosting](https://www.nuget.org/packages/Microsoft.Extensions.Hosting) NuGet package to configure and run clients (in addition to the silo). You also need the [Microsoft.Orleans.Client](https://www.nuget.org/packages/Microsoft.Orleans.Client) NuGet package. To use localhost clustering on the consuming client, call the <xref:Orleans.Hosting.ClientBuilderExtensions.UseLocalhostClustering*> extension method. Consider this example _Program.cs_ file for the client host:

```csharp
using Microsoft.Extensions.Hosting;

using IHost host = Host.CreateDefaultBuilder(args)
    .UseOrleansClient(client =>
    {
        client.UseLocalhostClustering();
    })
    .UseConsoleLifetime()
    .Build();

await host.StartAsync();
```

The preceding code does the following:

- Creates a default host builder.
- Calls the <xref:Microsoft.Extensions.Hosting.OrleansClientGenericHostExtensions.UseOrleansClient*> extension method to configure the client.
- Calls the `UseLocalhostClustering` extension method on the given <xref:Orleans.Hosting.IClientBuilder> to configure the client to use localhost clustering.
- Calls the `UseConsoleLifetime` extension method to configure the client to use the console lifetime.
- Calls the `StartAsync` method on the `host` variable to start the client.

:::zone-end

:::zone target="docs" pivot="orleans-3-x"

For local development, refer to the example below showing how to configure a client for this case. It configures a client that connects to a `loopback` silo.

Add the `Microsoft.Orleans.Client` NuGet meta-package to your project. After you become comfortable with the API, you can pick and choose the exact packages included in `Microsoft.Orleans.Client` that you need and reference them instead.

```powershell
Install-Package Microsoft.Orleans.Client
```

Configure <xref:Orleans.ClientBuilder> with a cluster ID matching the one specified for the local silo. Specify static clustering as your clustering choice, pointing it to the silo's gateway port.

The `ConfigureApplicationParts` call explicitly adds the assembly containing grain interfaces to the application setup.

After completing these steps, build the client and call its `Connect()` method to connect to the cluster.

You can create an empty console application project targeting .NET Framework 4.6.1 or higher for running a client, or reuse the console application project created for hosting the silo.

Here's an example of how a client can connect to a local silo:

:::code language="csharp" source="snippets-v3/local-dev/LocalDevelopment.cs" id="client_localhost":::

:::zone-end
