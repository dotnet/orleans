using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using Orleans.Metadata;
using System.Collections.Immutable;
using System.Collections.Concurrent;

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
        private readonly IServiceProvider _services;
        private readonly GrainPropertiesResolver _grainPropertiesResolver;
        private readonly ImmutableDictionary<string, Type> _strategies;
        private readonly PlacementStrategy _defaultPlacementStrategy;

        /// <summary>
        /// Create a <see cref="PlacementStrategyResolver"/> instance.
        /// </summary>
        public PlacementStrategyResolver(
            IServiceProvider services,
            IEnumerable<IPlacementStrategyResolver> resolvers,
            GrainPropertiesResolver grainPropertiesResolver)
        {
            _getStrategyInternal = GetPlacementStrategyInternal;
            _resolvers = resolvers.ToArray();
            _services = services;
            _grainPropertiesResolver = grainPropertiesResolver;
            _defaultPlacementStrategy = services.GetService<PlacementStrategy>();
            _strategies = GetAllStrategies(services);

            static ImmutableDictionary<string, Type> GetAllStrategies(IServiceProvider services)
            {
                var directors = services.GetRequiredService<IKeyedServiceCollection<Type, IPlacementDirector>>();
                var builder = ImmutableDictionary.CreateBuilder<string, Type>();
                foreach (var service in directors.GetServices(services))
                {
                    builder[service.Key.Name] = service.Key;
                }

                return builder.ToImmutable();
            }
        }

        /// <summary>
        /// Gets the placement strategy associated with the provided grain type.
        /// </summary>
        public PlacementStrategy GetPlacementStrategy(GrainType grainType) => _resolvedStrategies.GetOrAdd(grainType, _getStrategyInternal);

        internal bool TryGetNonDefaultPlacementStrategy(GrainType grainType, out PlacementStrategy strategy)
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
                if (_strategies.TryGetValue(placementStrategyId, out var strategyType))
                {
                    strategy = (PlacementStrategy)_services.GetService(strategyType);
                    strategy.Initialize(properties);
                    return true;
                }
                else
                {
                    throw new KeyNotFoundException($"Could not resolve placement strategy {placementStrategyId} for grain type {grainType}");
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
