---
title: Heterogeneous silos overview
description: Learn an overview of heterogeneous silos in .NET Orleans.
ms.date: 05/23/2025
ms.topic: overview
---

# Heterogeneous silos overview

On a given cluster, silos can support different sets of grain types:

:::image type="content" source="media/heterogeneous.png" alt-text="Heterogeneous silos overview diagram.":::

In this example, the cluster supports grains of type `A`, `B`, `C`, `D`, and `E`:

- Grain types `A` and `B` can be placed on Silo 1 and 2.
- Grain type `C` can be placed on Silo 1, 2, or 3.
- Grain type `D` can only be placed on Silo 3.
- Grain type `E` can only be placed on Silo 4.

All silos should reference the interfaces of all grain types in the cluster, but grain classes should only be referenced by the silos that host them. The client doesn't know which silo supports a given grain type.

> [!IMPORTANT]
> A given grain type implementation must be the same on each silo that supports it.

The following scenario is _not_ valid:

On Silo 1 and 2:

```csharp
public class C: Grain, IMyGrainInterface
{
   public Task SomeMethod() { /* ... */ }
}
```

On Silo 3:

```csharp
public class C: Grain, IMyGrainInterface, IMyOtherGrainInterface
{
   public Task SomeMethod() { /* ... */ }
   public Task SomeOtherMethod() { /* ... */ }
}
```

## Configuration

No configuration is needed; you can deploy different binaries on each silo in your cluster. However, if necessary, you can change the interval at which silos and clients check for changes in supported types using the <xref:Orleans.Configuration.TypeManagementOptions.TypeMapRefreshInterval?displayProperty=nameWithType> property.

For testing purposes, you can use the <xref:Orleans.Configuration.GrainClassOptions.ExcludedGrainTypes?displayProperty=nameWithType> property, which is a list of names of the types you want to exclude on specific silos.

## Limitations

- Connected clients aren't notified if the set of supported grain types changes. In the previous example:
  - If Silo 4 leaves the cluster, the client still tries to make calls to grains of type `E`. It fails at runtime with an <xref:Orleans.Runtime.OrleansException>.
  - If the client connected to the cluster before Silo 4 joined, the client cannot make calls to grains of type `E`. It fails with an <xref:System.ArgumentException>.
- Stateless grains aren't supported in heterogeneous deployments: all silos in the cluster must support the same set of stateless grains.
- <xref:Orleans.ImplicitStreamSubscriptionAttribute> isn't supported; thus, you can only use [Explicit subscriptions](../streaming/streams-programming-apis.md) in Orleans Streams with heterogeneous silos.
