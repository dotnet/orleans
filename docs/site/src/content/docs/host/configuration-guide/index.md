---
title: Orleans configuration guide
description: Configure Orleans silos, clients, providers, and endpoints.
ms.date: 08/02/2026
ms.topic: overview
---

# Orleans configuration guide

Orleans uses the [.NET Generic Host](../../../core/extensions/generic-host.md), dependency injection, and the [.NET options pattern](../../../core/extensions/options.md). Start with one of these hosting models:

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

Configure Orleans through `ISiloBuilder` or `IClientBuilder`:

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.UseOrleans(siloBuilder =>
{
    siloBuilder.Configure<ClusterOptions>(options =>
    {
        options.ServiceId = "orders";
        options.ClusterId = "orders-production";
    });

    // Add clustering, storage, reminders, and other providers here.
});

await builder.Build().RunAsync();
```

Provider extension methods validate configuration when the host starts. Prefer programmatic configuration when credentials require SDK objects such as `TokenCredential`, when configuration is computed, or when compile-time discoverability is important.

## Declarative configuration

Orleans automatically binds the `Orleans` configuration section when `UseOrleans()` or `UseOrleansClient()` is called. The following sections are recognized:

| Path | Applies to | Purpose |
|---|---|---|
| `Orleans` | Silo and client | `ClusterOptions`, including `ServiceId` and `ClusterId` |
| `Orleans:Name` | Silo | Silo name |
| `Orleans:Messaging` | Silo and client | `SiloMessagingOptions` or `ClientMessagingOptions` |
| `Orleans:Gateway` | Client | Gateway refresh and connection behavior |
| `Orleans:Endpoints` | Silo | Advertised and listening endpoints |
| `Orleans:Clustering` | Silo and client | One clustering provider |
| `Orleans:Reminders` | Silo | One reminder provider |
| `Orleans:BroadcastChannel:{name}` | Silo and client | Named broadcast-channel providers |
| `Orleans:Streaming:{name}` | Silo and client | Named stream providers |
| `Orleans:GrainStorage:{name}` | Silo | Named grain storage providers |
| `Orleans:GrainDirectory:{name}` | Silo | Named grain directory providers |

A provider section selects a registered provider with `ProviderType`. Install the provider's NuGet package so its configuration builder is discoverable:

```json
{
  "Orleans": {
    "ServiceId": "orders",
    "ClusterId": "orders-production",
    "Clustering": {
      "ProviderType": "Redis",
      "ConnectionString": "redis.example.com:6380,ssl=true"
    },
    "Endpoints": {
      "AdvertisedIPAddress": "10.0.0.12",
      "SiloPort": 11111,
      "GatewayPort": 30000,
      "SiloListeningEndpoint": "0.0.0.0:11111",
      "GatewayListeningEndpoint": "0.0.0.0:30000"
    },
    "GrainStorage": {
      "Default": {
        "ProviderType": "Redis",
        "ConnectionString": "redis.example.com:6380,ssl=true"
      }
    }
  }
}
```

Environment variables use double underscores, for example `Orleans__ClusterId` and `Orleans__Endpoints__SiloPort`.

> [!NOTE]
> Declarative provider names come from the installed provider assemblies. If Orleans reports an unknown `ProviderType`, verify that the corresponding package is referenced and use the provider name documented by that package.

## Configuration precedence

The Generic Host combines configuration providers in its normal order. Programmatic options configuration also participates in the options pipeline, so avoid configuring the same value in multiple places unless the override is intentional. Keep service and cluster identity stable and inject environment-specific endpoints, credentials, and provider connection details at deployment time.

## Production checklist

- Use a durable, shared clustering provider; don't use development or static clustering for a production cluster.
- Give every silo and client the same `ServiceId`, `ClusterId`, and clustering provider settings.
- Advertise addresses reachable by other silos and clients, especially behind NAT, containers, or load balancers.
- Use durable reminder and grain storage providers when the application depends on those features.
- Configure [server garbage collection](configuring-garbage-collection.md).
- Allow the Generic Host to perform [graceful shutdown](shutting-down-orleans.md).
- Validate provider connectivity and credentials before rollout.

For option types and API entry points, see [Core configuration options](list-of-options-classes.md) and the [`Orleans.Configuration` API reference](xref:Orleans.Configuration).
