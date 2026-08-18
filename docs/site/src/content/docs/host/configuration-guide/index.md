---
title: Orleans configuration guide
description: Configure Orleans silos, clients, providers, and endpoints.
ms.date: 08/18/2026
ms.topic: overview
---

# Orleans configuration guide

Orleans uses the [.NET Generic Host](https://learn.microsoft.com/dotnet/core/extensions/generic-host), [dependency injection](https://learn.microsoft.com/dotnet/core/extensions/dependency-injection), and the [.NET options pattern](https://learn.microsoft.com/dotnet/core/extensions/options). Start with one of these hosting models:

| Model | Use it when | Entry point |
|---|---|---|
| Silo with co-hosted client | The process hosts grains and also calls grains. This is the default for most services. | `builder.UseOrleans(...)` |
| External client | A separate process, such as a web frontend, calls a remote Orleans cluster but doesn't host grains. | `builder.UseOrleansClient(...)` |
| Aspire-orchestrated application | Aspire creates backing resources and injects Orleans configuration and service references. | `builder.AddOrleans(...)` in the AppHost, then parameterless `UseOrleans()` or `UseOrleansClient()` |

For a first local process, see [Local development configuration](local-development-configuration.md). For production, configure silos and external clients with the same cluster identity and clustering provider:

- [Server configuration](server-configuration.md)
- [Client configuration](client-configuration.md)
- [Typical configurations](typical-configurations.md)
- [Orleans and Aspire integration](../aspire-integration.md)

## Programmatic configuration

Create the [.NET Generic Host](https://learn.microsoft.com/dotnet/core/extensions/generic-host) with <xref:Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder*>, call <xref:Microsoft.Extensions.Hosting.OrleansSiloGenericHostExtensions.UseOrleans*> to add a silo, then build and run the host:

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="run_silo":::

Starting the host starts the silo and its co-hosted <xref:Orleans.IClusterClient>. The client is available through dependency injection, and stopping the host coordinates a [graceful Orleans shutdown](shutting-down-orleans.md).

Configure the silo through the <xref:Orleans.Hosting.ISiloBuilder> passed to `UseOrleans`. External client processes use <xref:Microsoft.Extensions.Hosting.OrleansClientGenericHostExtensions.UseOrleansClient*> and configure the <xref:Orleans.Hosting.IClientBuilder> passed to it. Provider extension methods validate configuration when the host starts. Programmatic configuration supports credentials supplied as SDK objects such as <xref:Azure.Core.TokenCredential>, computed configuration, and compile-time API discovery.

See [Server configuration](server-configuration.md) and [Client configuration](client-configuration.md) for compiled examples.

## Declarative configuration

Orleans automatically binds the `Orleans` configuration section when <xref:Microsoft.Extensions.Hosting.OrleansSiloGenericHostExtensions.UseOrleans*> or <xref:Microsoft.Extensions.Hosting.OrleansClientGenericHostExtensions.UseOrleansClient*> is called. The following sections are recognized:

| Path | Applies to | Purpose |
|---|---|---|
| `Orleans` | Silo and client | <xref:Orleans.Configuration.ClusterOptions>, including <xref:Orleans.Configuration.ClusterOptions.ServiceId> and <xref:Orleans.Configuration.ClusterOptions.ClusterId> |
| `Orleans:Name` | Silo | Silo name |
| `Orleans:Messaging` | Silo and client | <xref:Orleans.Configuration.SiloMessagingOptions> or <xref:Orleans.Configuration.ClientMessagingOptions> |
| `Orleans:Gateway` | Client | Gateway refresh and connection behavior |
| `Orleans:Endpoints` | Silo | Advertised and listening endpoints |
| `Orleans:Clustering` | Silo and client | One clustering provider |
| `Orleans:Reminders` | Silo | One reminder provider |
| `Orleans:BroadcastChannel:{name}` | Silo and client | Named broadcast-channel providers |
| `Orleans:Streaming:{name}` | Silo and client | Named stream providers |
| `Orleans:GrainStorage:{name}` | Silo | Named grain storage providers |
| `Orleans:GrainDirectory:{name}` | Silo | Named grain directory providers |

A provider section selects a registered provider with `ProviderType`. Install the provider's NuGet package so its configuration builder is discoverable. See [Server configuration](server-configuration.md#clustering-provider) for the provider catalog and [Typical configurations](typical-configurations.md) for deployment-oriented examples.

Environment variables use double underscores, for example `Orleans__ClusterId` and `Orleans__Endpoints__SiloPort`.

> [!NOTE]
> Declarative provider names come from the installed provider assemblies. If Orleans reports an unknown `ProviderType`, verify that the corresponding package is referenced and use the provider name documented by that package.

## Configuration precedence

The Generic Host combines [.NET configuration providers](https://learn.microsoft.com/dotnet/core/extensions/configuration-providers) in its normal order. Programmatic options configuration also participates in the options pipeline, so avoid configuring the same value in multiple places unless the override is intentional. Keep service and cluster identity stable and inject environment-specific endpoints, credentials, and provider connection details at deployment time.

## Production checklist

- Use a durable, shared clustering provider; don't use development or static clustering for a production cluster.
- Give every silo and client the same <xref:Orleans.Configuration.ClusterOptions.ServiceId>, <xref:Orleans.Configuration.ClusterOptions.ClusterId>, and clustering provider settings.
- Advertise addresses reachable by other silos and clients, especially behind NAT, containers, or load balancers.
- Use durable reminder and grain storage providers when the application depends on those features.
- Configure [server garbage collection](configuring-garbage-collection.md).
- Allow the Generic Host to perform [graceful shutdown](shutting-down-orleans.md).
- Validate provider connectivity and credentials before rollout.

For option types and API entry points, see [Core configuration options](list-of-options-classes.md) and <xref:Orleans.Configuration>.
