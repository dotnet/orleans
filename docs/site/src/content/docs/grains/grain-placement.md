---
title: Grain placement and migration
description: Understand placement, resource-optimized defaults, and activation movement in Orleans 10.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Grain placement and migration

When a grain isn't active, Orleans selects a compatible silo and creates an activation there. This process is **placement**. Callers continue using location-transparent grain references, so placement doesn't change application call sites.

## Default placement

<xref:Orleans.Runtime.ResourceOptimizedPlacement> is the default Orleans 10 placement strategy. It uses sampled silo runtime statistics and a power-of-k-choices algorithm to balance new activations while avoiding overloaded silos. It considers CPU, memory, available memory, activation count, and a preference for the local silo.

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
| <xref:Orleans.Concurrency.StatelessWorkerAttribute> | Uses local, scalable worker-pool placement. |

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

Placement filters reduce the compatible candidate set before the placement strategy selects a silo. They can express requirements or preferences based on silo metadata. Placement filters are experimental in Orleans 10 and produce diagnostic `ORLEANSEXP004`.

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

The attribute doesn't prevent explicit `MigrateOnIdle()` calls.

## Experimental automatic movement

Orleans 10 includes two opt-in experimental services:

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

## Custom placement

Custom placement strategies and directors are advanced runtime extensions. Implement them only when built-in strategies plus placement filters can't express the requirement. A director must handle membership changes, empty candidate sets, overloaded silos, and deterministic testing.

For implementation details and examples, inspect the built-in directors under [`src/Orleans.Runtime/Placement`](https://github.com/dotnet/orleans/tree/main/src/Orleans.Runtime/Placement).
