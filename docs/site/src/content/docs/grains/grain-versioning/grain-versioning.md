---
title: Grain interface versioning
description: Route grain calls during heterogeneous Orleans deployments using numeric interface versions.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Grain interface versioning

Grain interface versioning lets silos with different interface versions coexist during a deployment. It is a **numeric routing and placement policy**. It doesn't version persistent state, validate the structural compatibility of two .NET interfaces, or migrate data.

## Assign a version

Apply <xref:Orleans.CodeGeneration.VersionAttribute> to a grain interface:

```csharp
[Version(2)]
public interface ICartGrain : IGrainWithStringKey
{
    Task<Cart> GetAsync();
    Task AddAsync(Item item);
}
```

The value is an unsigned 16-bit integer. Interfaces without the attribute have version `0`. Use monotonically increasing values for successive contract revisions.

## How routing works

Every versioned request carries the numeric interface version used by its caller. Orleans:

1. Reads the versions supported by silos for that grain interface.
1. Applies a [compatibility strategy](compatible-grains.md) to determine which activation versions can process the requested version.
1. Applies a [selector strategy](version-selector-strategy.md) when a new activation needs placement.
1. Routes the request to a silo supporting one of the selected versions.

If a request reaches an existing activation whose version is incompatible, Orleans deactivates it with reason `IncompatibleRequest`, invalidates the stale address, and retries placement for a compatible activation.

> [!IMPORTANT]
> The runtime compatibility check compares version numbers only. Orleans doesn't inspect methods, parameter types, serializer contracts, or behavior to prove compatibility. The application must uphold the contract represented by the selected strategy. During development, opt in to the [Orleans contract compatibility analyzer](contract-compatibility-analyzer.md) to track RPC signatures and identities in source control.

## Configure cluster defaults

<xref:Orleans.Configuration.GrainVersioningOptions> defaults to <xref:Orleans.Versions.Compatibility.BackwardCompatible> and <xref:Orleans.Versions.Selector.AllCompatibleVersions>:

:::code language="csharp" source="./snippets/versioning/VersioningConfiguration.cs" id="configure_versioning":::

The configured strategy names resolve registered Orleans strategy services. Configure every silo consistently before a heterogeneous deployment.

Orleans also exposes runtime strategy changes through <xref:Orleans.IVersionManager>, implemented by the management grain. Changes can apply cluster-wide or to a specific <xref:Orleans.Runtime.GrainInterfaceType>. Runtime overrides are operational state: coordinate them carefully and reset them to configured defaults after the deployment.

## Scope and limitations

- Stateless worker grains aren't versioned.
- Streaming interfaces aren't versioned.
- State and storage schema evolution are separate responsibilities.
- Version routing only helps while all deployed implementations honor the declared compatibility contract.

See [deploying new grain versions](deploying-new-versions-of-grains.md) for a rolling-upgrade sequence.
