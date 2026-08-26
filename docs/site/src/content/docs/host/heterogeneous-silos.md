---
title: Heterogeneous Orleans silos
description: Host different Orleans grain types on different silos.
ms.date: 08/26/2026
ms.topic: how-to
---

# Heterogeneous Orleans silos

Silos in one cluster can host different sets of grain classes. This lets you isolate workloads, use specialized hardware, or deploy grain implementations independently while retaining one Orleans cluster.

:::image type="content" source="media/heterogeneous.png" alt-text="A cluster whose silos support different grain type sets.":::

All silos and clients should reference the interfaces and serialization contracts they can exchange. A silo should reference only the grain implementation assemblies it can host.

## Prefer assembly boundaries

The simplest model is to put specialized grain classes in separate projects and reference each implementation project only from the silo host that should run it. Orleans discovers supported grain classes from generated type metadata.

For example:

- General-purpose silos reference `Orders.Grains`.
- GPU silos reference `Recommendations.Grains`.
- Clients reference `Orders.Contracts` and `Recommendations.Contracts`, but neither implementation assembly.

The same grain implementation must be compatible across every silo that advertises support for that grain type.

## Configuration

When one binary can run in several roles, configure <xref:Orleans.Configuration.GrainTypeOptions.Classes>:

:::code language="csharp" source="snippets/hosting/HostingExamples.cs" id="configure_grain_types":::

<xref:Orleans.Configuration.GrainTypeOptions.Classes> is a set of <xref:System.Type> values. Use it to include the exact grain classes the process can host or remove discovered classes that a role must not host:

:::code language="csharp" source="snippets/hosting/HostingExamples.cs" id="exclude_grain_type":::

Don't use obsolete grain-class exclusion option names from earlier Orleans versions.

## Direct placement by capability

Use [silo metadata](configuration-guide/silo-metadata.md) and [grain placement filtering](../grains/grain-placement-filtering.md) when many silos load the same grain implementation but only some meet a placement requirement such as region, hardware, tenant, or reservation type.

Use heterogeneous grain type registration when a silo cannot host the implementation at all. Use placement filtering when it can host the type but placement should prefer or require metadata.

## Deployment rules

- Keep <xref:Orleans.Configuration.ClusterOptions.ServiceId>, <xref:Orleans.Configuration.ClusterOptions.ClusterId>, clustering, and protocol configuration consistent across all roles.
- Deploy at least one healthy silo for every supported grain type before the response deadline for calls targeting that type.
- Maintain capacity and redundancy independently for each specialized grain set.
- Roll out contract changes before implementations that require them.
- Avoid removing the last silo for a grain type while requests or durable work still target it.

Clients obtain cluster type information after connecting. <xref:Orleans.Configuration.TypeManagementOptions.EnableDeferredGrainTypeResolution> defaults to `true`, so a client can create a grain reference before the cluster manifest contains a compatible implementation. Calls through that reference wait for a manifest update and retain their original response deadline. An explicit grain class prefix continues to require an immediate match.

Set <xref:Orleans.Configuration.TypeManagementOptions.EnableDeferredGrainTypeResolution> to `false` when every grain reference must resolve against the current cluster manifest during creation.

## Limitations

- A request reaches its response deadline when no active silo advertises a compatible target grain type.
- Every silo that supports one grain type must use a compatible implementation and contract.
- Stateless worker grains should be consistently available across the cluster rather than split into incompatible heterogeneous sets.
- Implicit stream subscriptions require compatible grain availability; use [explicit subscriptions](../streaming/streams-programming-apis.md) when heterogeneous deployment makes ownership ambiguous.

Test topology changes with production-like role counts, especially the loss and replacement of the last silo supporting a type.
