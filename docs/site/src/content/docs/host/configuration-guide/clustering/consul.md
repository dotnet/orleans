---
title: Configure Consul clustering
description: Configure Orleans silos and clients to use Consul for cluster membership and gateway discovery.
ms.date: 08/05/2026
ms.topic: how-to
---

# Configure Consul clustering

Use the [`Microsoft.Orleans.Clustering.Consul`](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.Consul) package to store Orleans membership records in the [Consul key/value store](https://developer.hashicorp.com/consul/docs/dynamic-app-config/kv).

Every silo and client in a cluster must use:

- The same Consul cluster and key/value root.
- The same <xref:Orleans.Configuration.ClusterOptions.ServiceId>.
- The same <xref:Orleans.Configuration.ClusterOptions.ClusterId>.

## Configure a silo

Configure <xref:Orleans.Configuration.ConsulClusteringOptions> with the Consul address and, when Consul ACLs are enabled, an ACL token:

```csharp
var builder = Host.CreateApplicationBuilder(args);

var consulAddress = new Uri(
    builder.Configuration["Consul:Address"]
        ?? throw new InvalidOperationException("Consul:Address isn't configured."));
var consulToken = builder.Configuration["Consul:Token"];

builder.UseOrleans(siloBuilder =>
{
    siloBuilder
        .Configure<ClusterOptions>(options =>
        {
            options.ServiceId = "orders";
            options.ClusterId = "production";
        })
        .UseConsulSiloClustering(options =>
        {
            options.ConfigureConsulClient(consulAddress, consulToken);
            options.KvRootFolder = "orleans/orders";
        });
});

await builder.Build().RunAsync();
```

## Configure a client

Configure Orleans clients with the same Consul settings and cluster identity:

```csharp
var builder = Host.CreateApplicationBuilder(args);

var consulAddress = new Uri(
    builder.Configuration["Consul:Address"]
        ?? throw new InvalidOperationException("Consul:Address isn't configured."));
var consulToken = builder.Configuration["Consul:Token"];

builder.UseOrleansClient(clientBuilder =>
{
    clientBuilder
        .Configure<ClusterOptions>(options =>
        {
            options.ServiceId = "orders";
            options.ClusterId = "production";
        })
        .UseConsulClientClustering(options =>
        {
            options.ConfigureConsulClient(consulAddress, consulToken);
            options.KvRootFolder = "orleans/orders";
        });
});

await builder.Build().RunAsync();
```

## Production considerations

- Run Consul in a highly available configuration and follow the [Consul production deployment guidance](https://developer.hashicorp.com/consul/docs/deploy/server).
- Enable Consul ACLs and TLS. Supply tokens using workload identity, a secret store, or the platform's protected configuration mechanism; don't embed tokens in source or images.
- Restrict network access so only authorized silos and clients can reach the Consul API.
- Give the Orleans identity only the permissions required for its key/value prefix.
- Use a distinct `ClusterId` and key/value root for environments that must remain isolated.
- Monitor Consul availability, request latency, leadership changes, storage capacity, and ACL or TLS failures.
- Test cluster startup and recovery while Consul is degraded or unavailable. Avoid synchronized, unbounded retries.

Consul membership data coordinates cluster discovery and liveness. It doesn't contain grain application state and doesn't replace a grain storage provider. For provider selection criteria, see [Topology, networking, and clustering](../../../deployment/networking.md#choose-a-clustering-provider).
