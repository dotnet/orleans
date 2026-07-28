---
title: Use Consul as a membership provider
description: Learn how to use Consul as a membership provider in .NET Orleans.
ms.date: 05/23/2025
ms.topic: how-to
ms.custom: devops
---

# Use Consul as a membership provider

[Consul](https://www.consul.io) is a distributed, highly available, and data center-aware service discovery platform including simple service registration, health checking, failure detection, and key-value storage. It's built on the premise that every node in the data center runs a Consul agent acting as either a server or client. Each agent communicates via a scalable gossip protocol.

A detailed overview of Consul, including comparisons with similar solutions, is available at [What is Consul?](https://developer.hashicorp.com/consul).

Consul is written in [Go](https://go.dev) and is [open source](https://github.com/hashicorp/consul). Compiled downloads are available for [macOS X, FreeBSD, Linux, Solaris, and Windows](https://developer.hashicorp.com/consul/install).

## Why choose Consul?

As an [Orleans Membership Provider](../implementation/cluster-management.md), Consul is a good choice for delivering on-premises solutions that don't require customers to have existing infrastructure or a cooperative IT provider. Consul is a lightweight single executable with no dependencies, making it easy to build into a middleware solution. When using Consul for discovering, checking, and maintaining microservices, fully integrating with Orleans membership offers simplicity and ease of operation. Consul also provides a membership table (also known as "Orleans Custom System Store") that fully integrates with Orleans's [Cluster Management](../implementation/cluster-management.md).

## Set up Consul

Extensive documentation on setting up a stable Consul cluster is available in the [Consul documentation](https://developer.hashicorp.com/consul), so that information won't be repeated here. However, for convenience, this guide shows how to quickly get Orleans running with a standalone Consul agent.

1. Create a folder to install Consul into (for example _C:\Consul_).
1. Create a subfolder: _C:\Consul\Data_ (Consul doesn't create this directory if it doesn't exist).
1. [Download](https://developer.hashicorp.com/consul/install) and unzip _Consul.exe_ into _C:\Consul_.
1. Open a command prompt at _C:\Consul_ and run the following command:

   ```powershell
   ./consul.exe agent -server -bootstrap -data-dir "C:\Consul\Data" -client='0.0.0.0'
   ```

   In the preceding command:

   - `agent`: Instructs Consul to run the agent process hosting the services. Without this switch, the Consul process attempts to use RPC to configure a running agent.
   - `-server`: Defines the agent as a server, not a client. (A Consul _client_ is an agent hosting services and data but lacks voting rights and cannot become the cluster leader).
   - `-bootstrap`: The first (and only the first!) node in a cluster must be bootstrapped to assume cluster leadership.
   - `-data-dir [path]`: Specifies the path where all Consul data, including the cluster membership table, is stored.
   - `-client='0.0.0.0'`: Informs Consul which IP address to open the service on.

   Many other parameters exist, including the option to use a JSON configuration file. See the Consul documentation for a full listing.

1. Verify Consul is running and ready to accept membership requests from Orleans by opening the services endpoint in your browser at `http://localhost:8500/v1/catalog/services`. When functioning correctly, the browser displays the following JSON:

   ```json
   {
       "consul": []
   }
   ```

## Configure Orleans

To configure Orleans to use Consul as a membership provider, the silo project needs to reference the [Microsoft.Orleans.Clustering.Consul](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.Consul) NuGet package. After adding the reference, configure the membership provider in the silo's _Program.cs_ file as follows:

:::code source="snippets/consul/Silo/Program.cs":::

The preceding code:

- Creates an <xref:Microsoft.Extensions.Hosting.IHostBuilder> with defaults from <xref:Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder?displayProperty=nameWithType>.
- Chains a call to <xref:Microsoft.Extensions.Hosting.GenericHostExtensions.UseOrleans(Microsoft.Extensions.Hosting.IHostBuilder,System.Action{Microsoft.Extensions.Hosting.HostBuilderContext,Orleans.Hosting.ISiloBuilder})> to configure the Orleans silo.
- Given the <xref:Orleans.Hosting.ISiloBuilder>, calls <xref:Orleans.Hosting.ConsulUtilsHostingExtensions.UseConsulSiloClustering*>.
- Configures the cluster membership provider to use Consul, given the Consul `address`.

To configure the client, reference the same NuGet package and call the <xref:Orleans.Hosting.ConsulUtilsHostingExtensions.UseConsulClientClustering*> extension method.

## Client SDK

If interested in using Consul for service discovery, [Client SDKs](https://developer.hashicorp.com/consul/docs/integrate/consul-tools) are available for most popular languages.

### Implementation details

The Membership Table Provider uses [Consul's Key/Value store](https://developer.hashicorp.com/consul/api-docs/kv) functionality with Check-And-Set (CAS) operations. When each Silo starts, it registers two key-value entries: one containing Silo details and one holding the last time the Silo reported it was alive. The latter refers to diagnostic "I'm alive" entries, not failure detection heartbeats, which are sent directly between silos and aren't written to the table. All writes to the table use CAS to provide concurrency control, as required by Orleans's [Cluster Management Protocol](../implementation/cluster-management.md).

Once the Silo runs, view these entries in a web browser at `http://localhost:8500/v1/kv/?keys&pretty`. The output looks similar to this:

```json
[
    "orleans/default/192.168.1.11:11111@43165319",
    "orleans/default/192.168.1.11:11111@43165319/iamalive",
    "orleans/default/version"
]
```

All keys are prefixed with `orleans`. This prefix is hardcoded in the provider and intended to avoid keyspace collisions with other Consul users. Retrieve additional information for each key by appending its name (without quotes) to the Consul KV root at `http://localhost:8500/v1/kv/`. Doing so presents the following JSON:

```json
[
    {
        "LockIndex": 0,
        "Key": "orleans/default/192.168.1.11:11111@43165319",
        "Flags": 0,
        "Value": "[BASE64 UTF8 Encoded String]",
        "CreateIndex": 321,
        "ModifyIndex": 322
    }
]
```

Decoding the Base64 UTF-8 encoded string `Value` provides the actual Orleans membership data:

**`http://localhost:8500/v1/KV/orleans/default/[SiloAddress]`**

```json
{
    "Hostname": "[YOUR_MACHINE_NAME]",
    "ProxyPort": 30000,
    "StartTime": "2023-05-15T14:22:00.004977Z",
    "Status": 3,
    "SiloName": "Silo_fcad0",
    "SuspectingSilos": []
}
```

**`http://localhost:8500/v1/KV/orleans/default/[SiloAddress]/IAmAlive`**

```plaintext
"2023-05-15T14:27:01.1832828Z"
```

When clients connect, they read the KVs for all silos in the cluster in one HTTP GET request using the URI `http://localhost:8500/v1/KV/orleans/default/?recurse`.

## Limitations

Be aware of a few limitations when using Consul as a membership provider.

### Orleans extended membership protocol (table version and ETag)

Consul KV currently doesn't support atomic updates. Therefore, the Orleans Consul Membership Provider only implements the Orleans basic membership protocol, as described in [Cluster management in Orleans](../implementation/cluster-management.md). It doesn't support the Extended Membership Protocol. This Extended protocol was introduced as an extra, though not essential, silo connectivity validation and as a foundation for functionality not yet implemented.

### Multiple datacenters

Key-value pairs in Consul aren't currently replicated between Consul data centers. A [separate project](https://github.com/hashicorp/consul-replicate) exists to address this replication effort, but it hasn't yet been proven to support Orleans.

### When running on Windows

When Consul starts on Windows, it logs the following message:

```Output
==> WARNING: Windows is not recommended as a Consul server. Do not use in production.
```

This warning message appears due to a lack of focus on testing when running in a Windows environment, not because of any actual known issues. Read the [discussion](https://groups.google.com/forum/#!topic/consul-tool/DvXYgZtUZyU) before deciding if Consul is the right choice.

## Potential future enhancements

1. Prove the Consul KV replication project can support an Orleans cluster in a WAN environment between multiple Consul data centers.
1. Implement the Reminder Table in Consul.
1. Implement the Extended Membership Protocol.
The team behind Consul plans to implement atomic operations. Once this functionality is available, removing the limitations in the provider might be possible.
