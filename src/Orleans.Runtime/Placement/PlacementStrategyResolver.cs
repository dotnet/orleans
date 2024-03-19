using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using Orleans.Metadata;
using System.Collections.Immutable;
using System.Collections.Concurrent;
using Orleans.Runtime.Hosting;
using System.Collections.Frozen;
using Orleans.GrainDirectory;

namespace Orleans.Runtime.Placement
{
    /// <summary>
    /// Responsible for resolving an <see cref="PlacementStrategy"/> for a <see cref="GrainType"/>.
    /// </summary>
    public sealed class PlacementStrategyResolver
    {
        private readonly ConcurrentDictionary<GrainType, PlacementStrategy> _resolvedStrategies = new();
        private readonly Func<GrainType, PlacementStrategy> _getStrategyInternal;
        private readonly IPlacementStrategyResolver[] _resolvers;
        private readonly GrainPropertiesResolver _grainPropertiesResolver;
        private readonly PlacementStrategy _defaultPlacementStrategy;
        private readonly IServiceProvider _services;

        /// <summary>
        /// Create a <see cref="PlacementStrategyResolver"/> instance.
        /// </summary>
        public PlacementStrategyResolver(
            IServiceProvider services,
            IEnumerable<IPlacementStrategyResolver> resolvers,
            GrainPropertiesResolver grainPropertiesResolver)
        {
            _services = services;
            _getStrategyInternal = GetPlacementStrategyInternal;
            _resolvers = resolvers.ToArray();
            _grainPropertiesResolver = grainPropertiesResolver;
            _defaultPlacementStrategy = services.GetService<PlacementStrategy>();
        }

        /// <summary>
        /// Gets the placement strategy associated with the provided grain type.
        /// </summary>
        public PlacementStrategy GetPlacementStrategy(GrainType grainType) => _resolvedStrategies.GetOrAdd(grainType, _getStrategyInternal);

        private bool TryGetNonDefaultPlacementStrategy(GrainType grainType, out PlacementStrategy strategy)
        {
            _grainPropertiesResolver.TryGetGrainProperties(grainType, out var properties);

            foreach (var resolver in _resolvers)
            {
                if (resolver.TryResolvePlacementStrategy(grainType, properties, out strategy))
                {
                    return true;
                }
            }

            if (properties is not null
                && properties.Properties.TryGetValue(WellKnownGrainTypeProperties.PlacementStrategy, out var placementStrategyId)
                && !string.IsNullOrWhiteSpace(placementStrategyId))
            {
                strategy = _services.GetKeyedService<PlacementStrategy>(placementStrategyId);
                if (strategy is not null)
                {
                    strategy.Initialize(properties);
                    return true;
                }
                else
                {
                    throw new KeyNotFoundException($"Could not resolve placement strategy {placementStrategyId} for grain type {grainType}.");
                }
            }

            strategy = default;
            return false;
        }

        private PlacementStrategy GetPlacementStrategyInternal(GrainType grainType)
        {
            if (TryGetNonDefaultPlacementStrategy(grainType, out var result))
            {
                return result;
            }

            return _defaultPlacementStrategy;
        }
    }
}
