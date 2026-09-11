using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Metadata;

namespace Orleans.Runtime.Placement;

/// <summary>
/// Resolves cluster placement strategies for grain types.
/// </summary>
public sealed class ClusterPlacementStrategyResolver
{
    private readonly ConcurrentDictionary<GrainType, Entry> _resolvedStrategies = new();
    private readonly GrainPropertiesResolver _grainPropertiesResolver;
    private readonly IServiceProvider _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClusterPlacementStrategyResolver"/> class.
    /// </summary>
    public ClusterPlacementStrategyResolver(GrainPropertiesResolver grainPropertiesResolver, IServiceProvider services)
    {
        _grainPropertiesResolver = grainPropertiesResolver;
        _services = services;
    }

    /// <summary>
    /// Resolves the cluster placement strategy for the provided grain type.
    /// </summary>
    public ClusterPlacementStrategy? Resolve(GrainType grainType)
        => _resolvedStrategies.GetOrAdd(grainType, ResolveInternal).Value;

    private Entry ResolveInternal(GrainType grainType)
    {
        if (!_grainPropertiesResolver.TryGetGrainProperties(grainType, out var properties)
            || !properties.Properties.TryGetValue(WellKnownGrainTypeProperties.ClusterPlacementStrategy, out var strategyName)
            || string.IsNullOrWhiteSpace(strategyName))
        {
            return default;
        }

        var strategy = _services.GetKeyedService<ClusterPlacementStrategy>(strategyName)
            ?? throw new KeyNotFoundException($"Could not resolve cluster placement strategy '{strategyName}' for grain type '{grainType}'.");
        strategy.Initialize(properties);
        return new Entry(strategy);
    }

    private readonly record struct Entry(ClusterPlacementStrategy? Value);
}
