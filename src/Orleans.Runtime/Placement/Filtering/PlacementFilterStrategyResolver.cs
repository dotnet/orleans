using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Metadata;
using Orleans.Placement;

#nullable enable
namespace Orleans.Runtime.Placement.Filtering;

/// <summary>
/// Responsible for resolving an <see cref="PlacementFilterStrategy"/> for a <see cref="GrainType"/>.
/// </summary>
public sealed class PlacementFilterStrategyResolver
{
    private readonly ConcurrentDictionary<GrainType, PlacementFilterStrategy[]> _resolvedFilters = new();
    private readonly Func<GrainType, PlacementFilterStrategy[]> _getFiltersInternal;
    private readonly GrainPropertiesResolver _grainPropertiesResolver;
    private readonly IServiceProvider _services;

    /// <summary>
    /// Create a <see cref="PlacementFilterStrategyResolver"/> instance.
    /// </summary>
    public PlacementFilterStrategyResolver(
        IServiceProvider services,
        GrainPropertiesResolver grainPropertiesResolver)
    {
        _services = services;
        _getFiltersInternal = GetPlacementFilterStrategyInternal;
        _grainPropertiesResolver = grainPropertiesResolver;
    }

    /// <summary>
    /// Gets the placement filter strategy associated with the provided grain type.
    /// </summary>
    public PlacementFilterStrategy[] GetPlacementFilterStrategies(GrainType grainType) => _resolvedFilters.GetOrAdd(grainType, _getFiltersInternal);

    private PlacementFilterStrategy[] GetPlacementFilterStrategyInternal(GrainType grainType)
    {
        _grainPropertiesResolver.TryGetGrainProperties(grainType, out var properties);

        if (properties is not null
            && properties.Properties.TryGetValue(WellKnownGrainTypeProperties.PlacementFilter, out var placementFilterIds)
            && !string.IsNullOrWhiteSpace(placementFilterIds))
        {
            var filterList = new List<PlacementFilterStrategy>();
            foreach (var filterId in placementFilterIds.Split(","))
            {
                var filter = _services.GetKeyedService<PlacementFilterStrategy>(filterId);
                if (filter is not null)
                {
                    filter.Initialize(properties);
                    filterList.Add(filter);
                }
                else
                {
                    throw new KeyNotFoundException($"Could not resolve placement filter strategy {filterId} for grain type {grainType}. Ensure that dependencies for that filter have been configured in the Container. This is often through a .Use* extension method provided by the implementation.");
                }
            }

            var orderedFilters = filterList.OrderBy(f => f.Order).ToArray();
            // check that the order is unique
            if (orderedFilters.Select(f => f.Order).Distinct().Count() != orderedFilters.Length)
            {
                throw new InvalidOperationException($"Placement filters for grain type {grainType} have duplicate order values. Order values must be specified if more than one filter is applied and must be unique.");
            }
            return orderedFilters;
        }

        return [];
    }
}