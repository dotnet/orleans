using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainReferences;
using Orleans.Metadata;
using Orleans.Runtime.Placement;
using Orleans.Serialization.TypeSystem;

namespace Orleans.Runtime
{
    /// <summary>
    /// Centralized statistics on per-grain-type activation counts.
    /// </summary>
    internal class GrainCountStatistics
    {
        private static readonly Func<string, CounterStatistic> CreateCounter = Create;
        private readonly ConcurrentDictionary<string, CounterStatistic> _grainCounts = new();
        public CounterStatistic GetGrainCount(string grainTypeName) => _grainCounts.GetOrAdd(grainTypeName, CreateCounter);

        public IEnumerable<KeyValuePair<string, long>> GetSimpleGrainStatistics()
        {
            return _grainCounts
                .Select(s => new KeyValuePair<string, long>(s.Key, s.Value.GetCurrentValue()))
                .Where(p => p.Value > 0);
        }

        private static CounterStatistic Create(string grainTypeName)
        {
            var counterName = new StatisticName(StatisticNames.GRAIN_COUNTS_PER_GRAIN, grainTypeName);
            return CounterStatistic.FindOrCreate(counterName, false);
        }
    }

    /// <summary>
    /// Functionality which is shared between all instances of a grain type.
    /// </summary>
    public class GrainTypeSharedContext
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly CounterStatistic _grainCountsPerGrain;
        private readonly GrainCountStatistics _grainCountStatistics;
        private readonly Dictionary<Type, object> _components = new();
        private InternalGrainRuntime _internalGrainRuntime;

        public GrainTypeSharedContext(
            GrainType grainType,
            IClusterManifestProvider clusterManifestProvider,
            GrainClassMap grainClassMap,
            PlacementStrategyResolver placementStrategyResolver,
            IOptions<SiloMessagingOptions> messagingOptions,
            IOptions<GrainCollectionOptions> collectionOptions,
            IGrainRuntime grainRuntime,
            ILogger logger,
            GrainReferenceActivator grainReferenceActivator,
            IServiceProvider serviceProvider)
        {
            if (!grainClassMap.TryGetGrainClass(grainType, out var grainClass))
            {
                throw new KeyNotFoundException($"Could not find corresponding grain class for grain of type {grainType.ToString()}");
            }

            var grainTypeName = RuntimeTypeNameFormatter.Format(grainClass);
            _grainCountStatistics = serviceProvider.GetRequiredService<GrainCountStatistics>();
            _grainCountsPerGrain = _grainCountStatistics.GetGrainCount(grainTypeName);
            Logger = logger;
            MessagingOptions = messagingOptions.Value;
            GrainReferenceActivator = grainReferenceActivator;
            _serviceProvider = serviceProvider;
            MaxWarningRequestProcessingTime = messagingOptions.Value.ResponseTimeout.Multiply(5);
            MaxRequestProcessingTime = messagingOptions.Value.MaxRequestProcessingTime;
            PlacementStrategy = placementStrategyResolver.GetPlacementStrategy(grainType);
            Runtime = grainRuntime;

            CollectionAgeLimit = GetCollectionAgeLimit(
                grainType,
                grainClass,
                clusterManifestProvider.LocalGrainManifest,
                collectionOptions.Value);
        }

        private TimeSpan GetCollectionAgeLimit(GrainType grainType, Type grainClass, GrainManifest siloManifest, GrainCollectionOptions collectionOptions)
        {
            if (siloManifest.Grains.TryGetValue(grainType, out var properties)
                && properties.Properties.TryGetValue(WellKnownGrainTypeProperties.IdleDeactivationPeriod, out var idleTimeoutString))
            {
                if (string.Equals(idleTimeoutString, WellKnownGrainTypeProperties.IndefiniteIdleDeactivationPeriodValue))
                {
                    return Timeout.InfiniteTimeSpan;
                }

                if (TimeSpan.TryParse(idleTimeoutString, out var result))
                {
                    return result;
                }
            }

            if (collectionOptions.ClassSpecificCollectionAge.TryGetValue(grainClass.FullName, out var specified))
            {
                return specified;
            }

            return collectionOptions.CollectionAge;
        }

        /// <summary>
        /// Gets a component.
        /// </summary>
        /// <typeparam name="TComponent">The type specified in the corresponding <see cref="SetComponent{TComponent}"/> call.</typeparam>
        public TComponent GetComponent<TComponent>()
        {
            if (_components is null) return default;
            _components.TryGetValue(typeof(TComponent), out var resultObj);
            return (TComponent)resultObj;
        }

        /// <summary>
        /// Registers a component.
        /// </summary>
        /// <typeparam name="TComponent">The type which can be used as a key to <see cref="GetComponent{TComponent}"/>.</typeparam>
        public void SetComponent<TComponent>(TComponent instance)
        {
            if (instance == null)
            {
                _components.Remove(typeof(TComponent));
                return;
            }

            _components[typeof(TComponent)] = instance;
        }

        public TimeSpan CollectionAgeLimit { get; }
        public ILogger Logger { get; }

        public SiloMessagingOptions MessagingOptions { get; }
        public GrainReferenceActivator GrainReferenceActivator { get; }

        // This is the maximum amount of time we expect a request to continue processing
        public TimeSpan MaxRequestProcessingTime { get; }
        public TimeSpan MaxWarningRequestProcessingTime { get; }

        public PlacementStrategy PlacementStrategy { get; }
        public IGrainRuntime Runtime { get; }

        internal InternalGrainRuntime InternalRuntime => _internalGrainRuntime ??= _serviceProvider.GetRequiredService<InternalGrainRuntime>();

        public void OnCreateActivation(IGrainContext grainContext)
        {
            _grainCountsPerGrain.Increment();
        }

        public void OnDestroyActivation(IGrainContext grainContext)
        {
            _grainCountsPerGrain.DecrementBy(1);
        }
    }

    internal interface IActivationLifecycleObserver
    {
        void OnCreateActivation(IGrainContext grainContext);
        void OnDestroyActivation(IGrainContext grainContext);
    }
}
