---
title: Grain placement and migration
description: Understand placement, resource-optimized defaults, and activation movement in Orleans.
ms.date: 08/08/2026
ms.topic: concept-article
---

# Grain placement and migration

When a grain isn't active, Orleans selects a compatible silo and creates an activation there. This process is **placement**. Callers continue using location-transparent grain references, so placement doesn't change application call sites.

This article covers application-facing placement configuration. For the runtime algorithms and coordination protocols behind placement and activation movement, see [Placement and activation balancing](../implementation/load-balancing.md).

## Default placement

<xref:Orleans.Runtime.ResourceOptimizedPlacement> is the default placement strategy. It uses sampled silo runtime statistics and a power-of-k-choices algorithm to balance new activations while avoiding overloaded silos. It considers CPU, memory, available memory, activation count, and a preference for the local silo.

For design background on its resource scoring and signal smoothing, see [Resource-based placement with cooperative dual-mode Kalman filtering](https://www.ledjonbehluli.com/posts/orleans_resource_placement_kalman/).

Configure its weights through <xref:Orleans.Configuration.ResourceOptimizedPlacementOptions>:

```csharp
siloBuilder.Configure<ResourceOptimizedPlacementOptions>(options =>
{
    options.CpuUsageWeight = 40;
    options.MemoryUsageWeight = 20;
    options.AvailableMemoryWeight = 20;
    options.ActivationCountWeight = 15;
    options.LocalSiloPreferenceMargin = 5;
});
```

Weights are relative and don't need to total 100. Keep defaults until measurements show a workload-specific reason to change them.

## Per-grain strategies

Apply a placement attribute to a grain implementation when its requirements differ from the default:

| Attribute | Behavior |
|---|---|
| <xref:Orleans.Placement.ResourceOptimizedPlacementAttribute> | Explicitly selects the default resource-aware strategy. |
| <xref:Orleans.Placement.RandomPlacementAttribute> | Chooses a random compatible silo. |
| <xref:Orleans.Placement.PreferLocalPlacementAttribute> | Uses the local compatible silo when possible. |
| <xref:Orleans.Placement.HashBasedPlacementAttribute> | Maps the grain ID across the current compatible silo set. |
| <xref:Orleans.Placement.ActivationCountBasedPlacementAttribute> | Favors sampled silos with fewer activations. |
| <xref:Orleans.Placement.SiloRoleBasedPlacementAttribute> | Restricts placement by silo role. |
| <xref:Orleans.Concurrency.StatelessWorkerAttribute> | Uses local, scalable [worker-pool placement](stateless-worker-grains.md). |

Activation-count-based placement applies the power-of-two-choices technique described in [The Power of Two Choices in Randomized Load Balancing](https://www.eecs.harvard.edu/~michaelm/postscripts/mythesis.pdf).

```csharp
[PreferLocalPlacement]
public sealed class GatewayCacheGrain :
    Grain,
    IGatewayCacheGrain
{
}
```

Placement happens when creating an activation. Changing cluster membership or a strategy doesn't move existing activations by itself.

## Override the cluster default

Register a different default strategy only when all unannotated grains should use it:

```csharp
siloBuilder.Services.AddSingleton<
    PlacementStrategy,
    RandomPlacement>();
```

Per-grain attributes still take precedence.

## Placement filters

Placement filters reduce the compatible candidate set before the placement strategy selects a silo. They can express requirements or preferences based on [silo metadata](../host/configuration-guide/silo-metadata.md). The built-in metadata filter attributes are experimental and produce diagnostic `ORLEANSEXP004`.

See [Placement filters](grain-placement-filtering.md) for the built-in filters and experimental status.

## Request migration

A grain can ask Orleans to move its activation after current work completes:

```csharp
public Task Move()
{
    MigrateOnIdle();
    return Task.CompletedTask;
}
```

Migration is advisory and occurs only if placement chooses another compatible silo. Custom activation state must participate in dehydration and rehydration; see [Grain activation and lifecycle](grain-lifecycle.md#grain-migration).

Use <xref:Orleans.Placement.ImmovableAttribute> to exclude a grain class from automatic movement:

```csharp
[Immovable]
public sealed class HardwareSessionGrain :
    Grain,
    IHardwareSessionGrain
{
}
```

The attribute doesn't prevent explicit <xref:Orleans.Grain.MigrateOnIdle> calls.

## Experimental automatic movement

Orleans includes two opt-in experimental services:

| Feature | Goal | Diagnostic |
|---|---|---|
| Activation repartitioner | Improve grain-to-grain call locality. | `ORLEANSEXP001` |
| Activation rebalancer | Balance activation count and memory pressure across silos. | `ORLEANSEXP002` |

Enable them independently:

```csharp
#pragma warning disable ORLEANSEXP001
siloBuilder.AddActivationRepartitioner();
#pragma warning restore ORLEANSEXP001

#pragma warning disable ORLEANSEXP002
siloBuilder.AddActivationRebalancer();
#pragma warning restore ORLEANSEXP002
```

Both features migrate eligible activations and can operate together. They add cluster coordination and state-transfer costs, so benchmark representative workloads before production use. Stateless workers, system targets, grain services, client objects, and immovable activations aren't candidates.

## Implement custom placement

Custom placement strategies and directors are advanced runtime extensions. Implement them only when built-in strategies plus [placement filters](grain-placement-filtering.md) can't express the requirement. A custom implementation has three parts:

1. A <xref:Orleans.Runtime.PlacementStrategy> that identifies the policy.
1. A <xref:Orleans.Placement.PlacementAttribute> that applies the policy to a grain class.
1. An <xref:Orleans.Runtime.Placement.IPlacementDirector> that selects one candidate silo.

The following example gives related grain types an affinity for the same silo when they use the same grain key. This differs from <xref:Orleans.Runtime.HashBasedPlacement>, which hashes the complete grain ID, including its grain type.

First, define the strategy and its attribute:

:::code language="csharp" source="snippets/placement/CustomPlacement.cs" id="custom_placement_strategy":::

Placement strategy instances can cross runtime serialization boundaries. Use `[GenerateSerializer]`; if the strategy adds serializable state, assign stable `[Id(n)]` values to its members. The strategy in this example is immutable and has no serialized members.

Next, implement the director:

:::code language="csharp" source="snippets/placement/CustomPlacement.cs" id="custom_placement_director":::

Call <xref:Orleans.Runtime.Placement.IPlacementContext.GetCompatibleSilos*> instead of reconstructing cluster membership. It returns active silos which can host the grain type and satisfy interface-version compatibility, after placement filters have run. The current runtime throws if that process leaves no candidates; the explicit empty-set check also protects the modulo operation in tests or alternate context implementations. <xref:Orleans.Runtime.Placement.IPlacementDirector.GetPlacementHint*> accepts a request hint only when it names one of those candidates, so the example honors valid hints before applying its own policy.

The director sorts the candidate addresses before indexing them and uses the grain key's stable, uniform hash. Therefore, two grain types with the same key select the same silo only when they see the same candidate set:

:::code language="csharp" source="snippets/placement/CustomPlacement.cs" id="apply_custom_placement":::

This is an affinity, not durable pinning. Membership changes, silo restarts, or different compatibility and filter results can change the mapping. Existing activations don't move merely because a later placement decision maps elsewhere. The uniform hash is deterministic across cluster nodes but isn't cryptographic, so don't use placement as an authorization or isolation boundary.

Finally, register the strategy and director on every silo:

:::code language="csharp" source="snippets/placement/CustomPlacement.cs" id="register_custom_placement":::

This overload registers the stateless strategy and the director as keyed singletons. Other overloads can change the strategy lifetime, but the director remains a keyed singleton. Directors must therefore be thread-safe and use singleton-safe dependencies. If a strategy carries attribute configuration, preserve it through <xref:Orleans.Runtime.PlacementStrategy.PopulateGrainProperties*> and <xref:Orleans.Runtime.PlacementStrategy.Initialize*> and use a lifetime which doesn't share mutable configuration between grain types.

This example deliberately trades resource-aware balancing for affinity. If load is the primary concern, prefer <xref:Orleans.Runtime.ResourceOptimizedPlacement> or apply a filter and let a built-in placement strategy choose from the remaining candidates. For more implementations, inspect the built-in directors under [`src/Orleans.Runtime/Placement`](https://github.com/dotnet/orleans/tree/main/src/Orleans.Runtime/Placement), including [`HashBasedPlacementDirector`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Runtime/Placement/HashBasedPlacementDirector.cs).
