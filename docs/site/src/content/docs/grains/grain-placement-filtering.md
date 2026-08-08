---
title: Grain placement filters
description: Filter Orleans grain placement candidates using silo metadata.
ms.date: 08/08/2026
ms.topic: concept-article
---

# Grain placement filters

> [!WARNING]
> The built-in <xref:Orleans.Runtime.Placement.Filtering.RequiredMatchSiloMetadataPlacementFilterAttribute> and <xref:Orleans.Runtime.Placement.Filtering.PreferredMatchSiloMetadataPlacementFilterAttribute> are experimental. These attributes are annotated with `Experimental("ORLEANSEXP004")` and can change without notice.

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

## Implement a custom filter

Prefer the built-in filters when exact metadata matching is sufficient. A custom filter is useful for a policy with different semantics, such as a numeric threshold. It has three parts:

1. A <xref:Orleans.Placement.PlacementFilterStrategy> that carries configuration and a unique order.
1. A <xref:Orleans.Placement.PlacementFilterAttribute> that attaches the strategy to a grain class.
1. An <xref:Orleans.Placement.IPlacementFilterDirector> that returns a subset of the candidate <xref:Orleans.Runtime.SiloAddress> values.

The following example requires candidates to advertise a minimum logical core count in the `hardware.cores` silo metadata entry. First, define the attribute and strategy:

:::code language="csharp" source="snippets/placement/CustomPlacementFilter.cs" id="custom_placement_filter_strategy":::

Filter configuration is stored in the grain manifest rather than serialized with the attribute instance. The public parameterless constructor lets dependency injection create the strategy. <xref:Orleans.Placement.PlacementFilterStrategy.GetAdditionalGrainProperties*> writes configuration to the manifest, and <xref:Orleans.Placement.PlacementFilterStrategy.AdditionalInitialize*> restores and validates it on each silo.

Next, implement the director:

:::code language="csharp" source="snippets/placement/CustomPlacementFilter.cs" id="custom_placement_filter_director":::

The director excludes candidates with missing, malformed, or insufficient metadata. If none remain, placement fails instead of silently weakening the requirement. A preference filter should explicitly return an appropriate fallback subset when its preferred result is empty.

Apply the filter to a grain class. A placement strategy still chooses from the candidates which remain:

:::code language="csharp" source="snippets/placement/CustomPlacementFilter.cs" id="apply_custom_placement_filter":::

Finally, register the filter on every silo:

:::code language="csharp" source="snippets/placement/CustomPlacementFilter.cs" id="register_custom_placement_filter":::

<xref:Orleans.Placement.PlacementFilterExtensions.AddPlacementFilter*> requires a <xref:Microsoft.Extensions.DependencyInjection.ServiceLifetime> for the strategy. This example uses `Transient` because initialization mutates the strategy with grain-type-specific configuration. Orleans caches the resulting strategy per grain type. The director is always registered as a keyed singleton, regardless of the strategy lifetime, so it must be thread-safe and use singleton-safe dependencies.

Return only candidates from the input sequence, keep filtering fast and deterministic for the supplied data, and monitor placement failures caused by hard requirements. Silo metadata is operator-provided scheduling information, not live utilization data or a security boundary. Use resource-optimized placement for live load signals and enforce authorization independently.

During placement, read application request metadata from <xref:Orleans.Runtime.Placement.PlacementTarget.RequestContextData?displayProperty=nameWithType>; the static <xref:Orleans.Runtime.RequestContext> isn't populated because no activation exists yet.
