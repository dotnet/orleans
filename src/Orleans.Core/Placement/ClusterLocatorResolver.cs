using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Metadata;

namespace Orleans.Runtime.Placement;

/// <summary>
/// Resolves cluster locators for grain types.
/// </summary>
public sealed class ClusterLocatorResolver
{
    private readonly ConcurrentDictionary<GrainType, Entry> _resolvedLocators = new();
    private readonly GrainPropertiesResolver _grainPropertiesResolver;
    private readonly IServiceProvider _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClusterLocatorResolver"/> class.
    /// </summary>
    public ClusterLocatorResolver(GrainPropertiesResolver grainPropertiesResolver, IServiceProvider services)
    {
        _grainPropertiesResolver = grainPropertiesResolver;
        _services = services;
    }

    /// <summary>
    /// Resolves the cluster locator for the provided grain type.
    /// </summary>
    public IClusterLocator? Resolve(GrainType grainType)
        => _resolvedLocators.GetOrAdd(grainType, ResolveInternal).Value;

    private Entry ResolveInternal(GrainType grainType)
    {
        if (!_grainPropertiesResolver.TryGetGrainProperties(grainType, out var properties)
            || !properties.Properties.TryGetValue(WellKnownGrainTypeProperties.ClusterLocator, out var locatorName)
            || string.IsNullOrWhiteSpace(locatorName))
        {
            return default;
        }

        var locator = _services.GetKeyedService<IClusterLocator>(locatorName)
            ?? throw new KeyNotFoundException($"Could not resolve cluster locator '{locatorName}' for grain type '{grainType}'.");
        return new Entry(locator);
    }

    private readonly record struct Entry(IClusterLocator? Value);
}
