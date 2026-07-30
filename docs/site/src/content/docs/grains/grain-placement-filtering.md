---
title: Grain placement filtering
description: Learn about grain placement filtering in Microsoft Orleans.

ms.date: 01/22/2026
---

# Grain placement filtering

Grain placement filtering in Orleans allows developers additional control over the placement of grains within a cluster. It works in conjunction with placement strategies, adding an additional layer of filtering to determine candidate silos for grain activation.

This filtering takes place before candidate silos are passed on to the configured placement method allowing for more flexibility and reuse of the filters.

For example, the existing `PreferLocal` placement strategy is hard coded to fall back to `Random` placement if the local silo is unable to host the grain type. But by using filters, a `PreferLocalPlacementFilter` could be implemented to filter down to either the local silo or all compatible silos. Then any placement strategy (`Random`, <xref:Orleans.Runtime.ResourceOptimizedPlacement>, `ActivationCount`, etc.) could be configured for that grain type. This allows for any set of filters and any placement strategy to be configured for a grain type.

## How placement filtering works

Placement filtering operates as an additional step in the grain placement process. After all compatible silos for the grain type are identified, all placement filters configured for that grain type, if any, are applied to allow further refinement of the selection by eliminating silos that do not meet the defined criteria.

### Ordering

Filters running in different orders may result in different behavior so explicit ordering is required when two or more filters are defined on a type. This needs to be configured with the `order:` parameter, as the type metadata pulled at runtime may return the attributes on a type in a different order from how they appear in the source code. Ordering must have unique values so an explicit ordering can be determined.

## Built-in filters

Orleans provides various built-in filters for you to choose from. However, if you are unable to find one that suits your needs you can always [implement your own](#implement-placement-filters).

### Silo metadata filters

These filters work with [*Silo Metadata*](../host/configuration-guide/silo-metadata.md) to filter candidate silos. The filters compare metadata values between the **calling silo** (the silo making the placement request) and **candidate silos**. Only silos whose metadata values match the calling silo's values for the specified keys are considered for placement. This enables locality-aware placement where grains are placed on silos in the same zone, rack, or region as the caller.

Before using these filters, configure metadata on your silos:

```csharp
// From environment variables (ORLEANS__METADATA__key=value)
builder.UseSiloMetadata();

// From IConfiguration
builder.UseSiloMetadata(configuration.GetSection("Orleans:Metadata"));

// From a dictionary
builder.UseSiloMetadata(new Dictionary<string, string>
{
    ["zone"] = "us-east-1a",
    ["tier"] = "premium",
    ["rack"] = "rack-42"
});
```

#### `RequiredMatchSiloMetadata`

Filters candidate silos to only those that match all of the specified metadata keys with the calling silo. If no compatible silos match all of the keys, an empty set is returned and placement fails for the grain.

Use required match filtering when a grain **must** be placed on a silo with specific metadata:

```csharp
// Grain will only be placed on silos where the "zone" metadata 
// matches the calling silo's "zone" value
[RequiredMatchSiloMetadataPlacementFilter(new[] { "zone" })]
public class ZoneRestrictedGrain : Grain, IZoneRestrictedGrain
{
    // This grain will only activate on silos in the same zone as the caller
}

// Multiple metadata keys can be specified
[RequiredMatchSiloMetadataPlacementFilter(new[] { "zone", "tier" })]
public class ZoneAndTierGrain : Grain, IZoneAndTierGrain
{
    // Requires both zone AND tier to match the calling silo's values
}
```

#### `PreferredMatchSiloMetadata`

This filter attempts to select only silos that match all of the configured metadata keys with the values of the calling silo. However, instead of returning an empty set if there are no matches (as with required filtering), this falls back to partial matches. The first configured metadata key is dropped and a match is made against the remaining keys. This continues, dropping the initial keys, until a sufficient number of matches are made. If no compatible silos match *any* of the metadata keys, all candidate silos are returned.

The `minCandidates` value configures how many candidates must be found to stop the filtering process. This value prevents a single silo or small number of silos from getting quickly overloaded if the match were that small.

If `minCandidates` were not considered, there could be a scenario where there are a large number of silos but only one silo that best matches the configured metadata keys. All placements would be concentrated on that one silo despite having many more available that could host activations. The purpose of `minCandidates` is to allow for a balance between preferring only best matches and avoiding hot silos. It is often desirable to not focus activation (or do scheduling in general) on one target. Set it to a value larger than 1 to ensure a minimum candidate set size so that future placement decisions can avoid concentrating activations on one or a few hot silos. Note that this config is a minimum value; more candidates could be returned. If you prefer most specific matching only, set `minCandidates: 1` to always prefer best match. This might be preferable in specific use cases where there is low activation throughput and where there is a great penalty when moving to a less specific match from a more specific one. In general use, the default value of 2 should be used (and not need to be specified in the attribute).

For example, if filtering on `[PreferredMatchSiloMetadataPlacementFilter(["cloud.availability-zone", "cloud.region"], minCandidates: 2)]` and there is only one matching silo on both `cloud.availability-zone` and `cloud.region`, then this scenario with `minCandidates: 2` would fail to match on both keys because only one silo matches both metadata keys and that's below the minimum configured size of 2. It would then fall back to matching only on `cloud.region`. If there were 2 or more silos that match only `cloud.region`, those would get returned. Otherwise, it would fall back to returning all of the candidates.

```csharp
// Prefer silos with matching "zone" and "rack" values, but allow fallback
// minCandidates ensures at least 2 silos are considered even without matches
[PreferredMatchSiloMetadataPlacementFilter(new[] { "zone", "rack" }, minCandidates: 2)]
public class LocalityAwareGrain : Grain, ILocalityAwareGrain
{
    // Prefers silos in the same zone/rack as the caller, but can activate elsewhere
}
```

### Access silo metadata at runtime

Grains can access metadata for any silo using `ISiloMetadataCache`:

```csharp
public class MyGrain : Grain, IMyGrain
{
    private readonly ISiloMetadataCache _metadataCache;

    public MyGrain(ISiloMetadataCache metadataCache)
    {
        _metadataCache = metadataCache;
    }

    public Task<string?> GetCurrentSiloZone()
    {
        var siloAddress = this.GetSiloAddress();
        var metadata = _metadataCache.GetMetadata(siloAddress);
        return Task.FromResult(metadata?.Metadata.GetValueOrDefault("zone"));
    }
}
```

## Implement placement filters

To implement a custom placement filter in Orleans, follow these steps:

1. **Implementation**
   - Create marker Attribute derived from `PlacementFilterAttribute`
   - Create Strategy derived from `PlacementFilterStrategy` to manage any configuration values
   - Create Director derived from `IPlacementFilterDirector` which contains the filtering logic
     - Define the filtering logic in the `Filter` method, which takes a list of candidate silos and returns a filtered list.

2. **Register the Filter**
   - Call `AddPlacementFilter` to register the Strategy and corresponding Director

3. **Apply the Filter**
   - Add the Attribute to a grain class to apply the filter

Here is an example of a simple custom Placement Filter. It is similar in behavior to using `[PreferLocalPlacement]` without any filter, but this has the advantage of being able to specify any placement method. Whereas <xref:Orleans.Runtime.PreferLocalPlacement> falls back to Random placement if the local silo is unable to host a grain, this example has configured <xref:Orleans.Runtime.ActivationCountBasedPlacement>. Any other placement could similarly be used with this filter

```csharp
[AttributeUsage(
    AttributeTargets.Class, AllowMultiple = false)]
public class ExamplePreferLocalPlacementFilterAttribute(int order)
    : PlacementFilterAttribute(
        new ExamplePreferLocalPlacementFilterStrategy(order));
```

```csharp
public class ExamplePreferLocalPlacementFilterStrategy(int order)
    : PlacementFilterStrategy(order)
    {
        public ExamplePreferLocalPlacementFilterStrategy() : this(0) { }
    }
```

```csharp
internal class ExamplePreferLocalPlacementFilterDirector(
    ILocalSiloDetails localSiloDetails)
    : IPlacementFilterDirector
{
    public IEnumerable<SiloAddress> Filter(PlacementFilterStrategy filterStrategy, PlacementTarget target, IEnumerable<SiloAddress> silos)
    {
        var siloList = silos.ToList();
        var localSilo = siloList.FirstOrDefault(s => s == localSiloDetails.SiloAddress);
        if (localSilo is not null)
        {
            return [localSilo];
        }
        return siloList;
    }
}
```

After implementing this filter, it can be registered and applied to grains.

```csharp
builder.Services.AddPlacementFilter<
    ExamplePreferLocalPlacementFilterStrategy,
    ExamplePreferLocalPlacementFilterDirector>();
```

```csharp
[ExamplePreferLocalPlacementFilter]
[ActivationCountBasedPlacement]
public class MyGrain() : Grain, IMyGrain
{
    // ...
}
```
