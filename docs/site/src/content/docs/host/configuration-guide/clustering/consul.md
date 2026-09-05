---
title: Configure Consul clustering
description: Configure Orleans silos and clients to use Consul for cluster membership and gateway discovery.
ms.date: 08/26/2026
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

:::code language="csharp" source="../../../snippets/compiled/Host/HostSnippets.cs" id="configure_consul_silo":::

## Configure a client

Configure Orleans clients with the same Consul settings and cluster identity:

:::code language="csharp" source="../../../snippets/compiled/Host/HostSnippets.cs" id="configure_consul_client":::

## Inspect membership data

The provider stores each cluster beneath one Consul key/value prefix:

- `<KvRootFolder>/orleans/<ClusterId>` when <xref:Orleans.Configuration.ConsulClusteringOptions.KvRootFolder?displayProperty=nameWithType> is configured.
- `orleans/<ClusterId>` when `KvRootFolder` isn't configured.

`ServiceId` isn't part of this prefix. Don't reuse the same `KvRootFolder` and `ClusterId` for different services, because their membership records would share one keyspace.

Under the cluster prefix, the provider maintains:

| Key | Purpose |
| --- | --- |
| `version` | The integer membership table version. Consul's `ModifyIndex` for this key is the compare-and-set ETag. |
| `<silo-address>` | The silo registration, including its host name, gateway port, start time, status, silo name, failure-detector votes, and metadata. |
| `<silo-address>/iamalive` | The silo's periodic `IAmAlive` timestamp. |

Membership-row changes and the corresponding version change use a [Consul transaction](https://developer.hashicorp.com/consul/api-docs/txn) with compare-and-set operations. An `IAmAlive` update writes its separate timestamp key and doesn't advance the table version. If a mixed-version rolling upgrade allows an older silo to replace its registration without the inline metadata field, an active metadata-aware silo conditionally restores its own metadata during its next `IAmAlive` update. This value supports diagnostics and startup recovery; it isn't the direct heartbeat used to detect a failed silo. Silos probe one another for failure detection, as described in [Cluster membership](../../../implementation/cluster-management.md).

Orleans clients list the cluster prefix and select active registrations with a nonzero gateway port. If a client discovers no gateways, inspect the exact prefix used by the client and silos, then compare registration status, gateway ports, and advertised-address reachability.

Use the [Consul KV command](https://developer.hashicorp.com/consul/commands/kv/get) to list or inspect the records:

```bash
consul kv get -keys -recurse <cluster-prefix>
consul kv get -detailed -recurse <cluster-prefix>
```

The detailed output includes each key's value and `ModifyIndex`. Treat the layout as a diagnostic implementation detail. Don't manually edit or delete membership keys while any silo from that cluster might still be running.

## Production considerations

- Run Consul in a highly available configuration and follow the [Consul production deployment guidance](https://developer.hashicorp.com/consul/docs/deploy/server).
- Enable Consul ACLs and TLS. Supply tokens using workload identity, a secret store, or the platform's protected configuration mechanism; don't embed tokens in source or images.
- Restrict network access so only authorized silos and clients can reach the Consul API.
- Give the Orleans identity only the permissions required for its key/value prefix.
- Use a distinct `ClusterId` and key/value root for environments that must remain isolated.
- Monitor Consul availability, request latency, leadership changes, storage capacity, and ACL or TLS failures.
- Test cluster startup and recovery while Consul is degraded or unavailable. Avoid synchronized, unbounded retries.

Consul membership data coordinates cluster discovery and liveness. It doesn't contain grain application state and doesn't replace a grain storage provider. For provider selection criteria, see [Topology, networking, and clustering](../../../deployment/networking.md#choose-a-clustering-provider).
