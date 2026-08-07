---
title: Grain placement filters
description: Filter Orleans grain placement candidates using silo metadata.
ms.date: 08/07/2026
ms.topic: concept-article
---

# Grain placement filters

> [!WARNING]
> Placement filtering is experimental. Its APIs are annotated with `Experimental("ORLEANSEXP004")` and can change without notice.

Placement first determines which silos are compatible with a grain type. Filters then reduce that candidate set, in order, before the grain's [placement strategy](grain-placement.md) selects a target.

Use filters for constraints and preferences such as availability zone, hardware capability, or deployment tier. Don't use them to encode per-request business routing that would make one grain identity behave like multiple independent entities.

## Configure silo metadata

Built-in filters compare [silo metadata](../host/configuration-guide/silo-metadata.md) on the calling silo with metadata on candidate silos. Configure consistent keys and values on every participating silo:

```csharp
siloBuilder.UseSiloMetadata(
    new Dictionary<string, string>
    {
        ["zone"] = "west-1",
        ["tier"] = "premium"
    });
```

External clients don't have silo metadata. Design filtered grain activation paths so the initiating placement request comes from a silo with the required metadata.

## Require metadata matches

<xref:Orleans.Runtime.Placement.Filtering.RequiredMatchSiloMetadataPlacementFilterAttribute> keeps only candidates matching every configured key:

```csharp
#pragma warning disable ORLEANSEXP004
[RequiredMatchSiloMetadataPlacementFilter(
    ["zone", "tier"])]
public sealed class PremiumZoneGrain :
    Grain,
    IPremiumZoneGrain
{
}
#pragma warning restore ORLEANSEXP004
```

Placement fails if no compatible silo matches. Use this filter only for hard requirements.

## Prefer metadata matches

<xref:Orleans.Runtime.Placement.Filtering.PreferredMatchSiloMetadataPlacementFilterAttribute> prefers candidates matching the ordered keys and progressively falls back by dropping earlier keys:

```csharp
#pragma warning disable ORLEANSEXP004
[PreferredMatchSiloMetadataPlacementFilter(
    ["rack", "zone"],
    minCandidates: 2)]
public sealed class LocalityGrain :
    Grain,
    ILocalityGrain
{
}
#pragma warning restore ORLEANSEXP004
```

`minCandidates` prevents a narrow preference from concentrating placements on too few silos. Its default is 2.

## Combine and order filters

When a grain class has multiple filters, give each a unique `order` value. Orleans applies lower values first. Attribute declaration order isn't a reliable ordering mechanism.

```csharp
#pragma warning disable ORLEANSEXP004
[RequiredMatchSiloMetadataPlacementFilter(
    ["tier"],
    order: 0)]
[PreferredMatchSiloMetadataPlacementFilter(
    ["rack", "zone"],
    minCandidates: 2,
    order: 10)]
public sealed class OrderedFilterGrain :
    Grain,
    IOrderedFilterGrain
{
}
#pragma warning restore ORLEANSEXP004
```

The placement strategy sees only candidates that remain after every filter. A preferred filter can broaden only within the candidate set it receives; it can't restore candidates removed by an earlier required filter.

## Read metadata from a grain

Inject <xref:Orleans.Runtime.MembershipService.SiloMetadata.ISiloMetadataCache> and <xref:Orleans.Runtime.IGrainRuntime> when grain logic needs silo metadata. Use <xref:Orleans.Runtime.IGrainRuntime.SiloAddress?displayProperty=nameWithType> to identify the current activation's silo. Metadata reads don't influence placement retroactively.

## Custom filters

A custom filter consists of:

1. A <xref:Orleans.Placement.PlacementFilterStrategy> carrying serializable configuration and a unique order.
1. A <xref:Orleans.Placement.PlacementFilterAttribute> that attaches the strategy to a grain class.
1. An <xref:Orleans.Placement.IPlacementFilterDirector> that returns a subset of candidate <xref:Orleans.Runtime.SiloAddress> values.
1. Registration from the silo's service collection, including the strategy lifetime:

    ```csharp
    siloBuilder.Services.AddPlacementFilter<
        ExamplePlacementFilterStrategy,
        ExamplePlacementFilterDirector>(
            ServiceLifetime.Transient);
    ```

    The <xref:Microsoft.Extensions.DependencyInjection.ServiceLifetime> argument controls the placement strategy lifetime. Orleans always registers the director as a keyed singleton.

Custom filters are part of the same `ORLEANSEXP004` API surface. Keep directors deterministic and fast, don't return silos outside the input candidate set, and define behavior for no matches. Prefer the built-in metadata filters unless custom logic is essential.

During placement, read application request metadata from <xref:Orleans.Runtime.Placement.PlacementTarget.RequestContextData?displayProperty=nameWithType>; the static <xref:Orleans.Runtime.RequestContext> isn't populated because no activation exists yet.
