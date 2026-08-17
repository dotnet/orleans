---
title: Server configuration
description: Configure Orleans silos, providers, and network endpoints.
ms.date: 08/15/2026
ms.topic: how-to
---

# Server configuration

Install [Microsoft.Orleans.Server](https://www.nuget.org/packages/Microsoft.Orleans.Server) and add Orleans to a [.NET Generic Host](https://learn.microsoft.com/dotnet/core/extensions/generic-host):

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="redis_silo":::

<xref:Microsoft.Extensions.Hosting.OrleansSiloGenericHostExtensions.UseOrleans*> hosts a silo and registers a co-hosted <xref:Orleans.IClusterClient>. Use <xref:Microsoft.Extensions.Hosting.OrleansClientGenericHostExtensions.UseOrleansClient*> only in a process that doesn't host grains.

## Clustering provider

Every silo and external client must use the same <xref:Orleans.Configuration.ClusterOptions.ServiceId>, <xref:Orleans.Configuration.ClusterOptions.ClusterId>, and clustering backend. Install the package for the deployment platform:

| Backend | Typical package or integration | Notes |
|---|---|---|
| Azure Table Storage | [`Microsoft.Orleans.Clustering.AzureStorage`](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.AzureStorage) | Supports Microsoft Entra credentials and connection strings. |
| ADO.NET | [`Microsoft.Orleans.Clustering.AdoNet`](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.AdoNet) | Supports SQL Server, PostgreSQL, MySQL/MariaDB, and Oracle. |
| Redis | [`Microsoft.Orleans.Clustering.Redis`](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.Redis) | Can share a managed or self-hosted Redis service. |
| Azure Cosmos DB | [`Microsoft.Orleans.Clustering.Cosmos`](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.Cosmos) | Uses a Cosmos DB container for membership. |
| DynamoDB | [`Microsoft.Orleans.Clustering.DynamoDB`](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.DynamoDB) | Common for AWS deployments. |
| Consul | [`Microsoft.Orleans.Clustering.Consul`](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.Consul) | Uses Consul key/value storage. |
| ZooKeeper | [`Microsoft.Orleans.Clustering.ZooKeeper`](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.ZooKeeper) | Uses a ZooKeeper ensemble. |

Use <xref:Orleans.Hosting.CoreHostingExtensions.UseLocalhostClustering*>, development clustering, or static gateways only for local development and tests.

[`Microsoft.Orleans.Hosting.Kubernetes`](https://www.nuget.org/packages/Microsoft.Orleans.Hosting.Kubernetes) configures a silo from its pod environment through <xref:Orleans.Hosting.KubernetesHostingExtensions.UseKubernetesHosting*>; it is not a clustering provider. Kubernetes deployments still need one of the shared clustering providers above.

Clustering stores membership, not grain state. Configure grain storage and reminders separately when the application uses them. Provider packages expose `Use...Clustering`, `Add...GrainStorage`, and `Use...ReminderService` methods and can also participate in [declarative configuration](index.md#declarative-configuration).

## Orleans clustering information

<xref:Orleans.Configuration.ClusterOptions.ServiceId> identifies the logical application and namespaces provider data. Keep it stable for the lifetime of the application. <xref:Orleans.Configuration.ClusterOptions.ClusterId> identifies a specific cluster, such as `orders-production` or `orders-green`. All participants in one cluster must agree on both values.

## Endpoints

A silo has two advertised endpoints:

- The silo endpoint is used for silo-to-silo traffic. Its default port is `11111`.
- The gateway endpoint is used by external clients. Its default port is `30000`; set it to `0` to disable the gateway.

Orleans must also know the IP address to advertise. If <xref:Orleans.Configuration.EndpointOptions.AdvertisedIPAddress> isn't configured, Orleans selects a local address and falls back to loopback if necessary. The listening endpoints default to the advertised address and corresponding advertised port; Orleans does **not** listen on every interface unless you configure wildcard listening endpoints.

For a directly reachable host, the helper configures advertised ports and an address:

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="direct_endpoints":::

For containers, NAT, or port forwarding, configure advertised and listening endpoints independently:

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="advertised_and_listening_endpoints":::

This silo listens on ports `40000` and `50000` but publishes `172.16.0.42:11111` and `172.16.0.42:30000`. Ensure membership data never contains an address that peers can't route to.

For private networking, host-port mappings, and cross-host container diagnostics, see [Run Orleans in containers across multiple hosts](../../deployment/containers.md).

## Configure providers and options

Use named providers when grain types need different stores:

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="named_providers":::

Configure runtime behavior with the options pattern:

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="membership_options":::

Prefer defaults until measurements or deployment requirements justify a change. See [Core configuration options](list-of-options-classes.md) rather than copying every property into application configuration.

## Production guidance

- Use workload identity, managed identity, or another short-lived credential mechanism where the provider supports it.
- Keep connection strings and credentials outside source control.
- Run at least three silos across failure domains when availability requirements demand quorum-like failure tolerance.
- Configure readiness so traffic starts only after host startup completes.
- Let the Generic Host receive termination signals and complete [graceful shutdown](shutting-down-orleans.md).
- Choose the [.NET GC mode](configuring-garbage-collection.md) and size CPU/memory limits from representative load tests.
