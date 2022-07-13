using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainReferences;
using Orleans.Metadata;
using Orleans.Runtime.Placement;
using Orleans.Serialization.TypeSystem;
using Orleans.Statistics;

namespace Orleans.Runtime
{
    /// <summary>
    /// Functionality which is shared between all instances of a grain type.
    /// </summary>
    public class GrainTypeSharedContext
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly string _grainTypeName;
        private readonly Dictionary<Type, object> _components = new();
        private InternalGrainRuntime _internalGrainRuntime;

        public GrainTypeSharedContext(
            GrainType grainType,
            IClusterManifestProvider clusterManifestProvider,
            GrainClassMap grainClassMap,
            PlacementStrategyResolver placementStrategyResolver,
            IOptions<SiloMessagingOptions> messagingOptions,
            IOptions<GrainCollectionOptions> collectionOptions,
            IOptions<SchedulingOptions> schedulingOptions,
            IGrainRuntime grainRuntime,
            ILogger logger,
            GrainReferenceActivator grainReferenceActivator,
            IServiceProvider serviceProvider)
        {
            if (!grainClassMap.TryGetGrainClass(grainType, out var grainClass))
            {
                throw new KeyNotFoundException($"Could not find corresponding grain class for grain of type {grainType.ToString()}");
            }

            _grainTypeName = RuntimeTypeNameFormatter.Format(grainClass);
            Logger = logger;
            MessagingOptions = messagingOptions.Value;
            GrainReferenceActivator = grainReferenceActivator;
            _serviceProvider = serviceProvider;
            MaxWarningRequestProcessingTime = messagingOptions.Value.ResponseTimeout.Multiply(5);
            MaxRequestProcessingTime = messagingOptions.Value.MaxRequestProcessingTime;
            PlacementStrategy = placementStrategyResolver.GetPlacementStrategy(grainType);
            SchedulingOptions = schedulingOptions.Value;
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
            if (typeof(TComponent) == typeof(PlacementStrategy) && PlacementStrategy is TComponent component)
            {
                return component;
            }

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
        public SchedulingOptions SchedulingOptions { get; }
        public IGrainRuntime Runtime { get; }

        internal InternalGrainRuntime InternalRuntime => _internalGrainRuntime ??= _serviceProvider.GetRequiredService<InternalGrainRuntime>();

        public void OnCreateActivation(IGrainContext grainContext)
        {
            GrainInstruments.IncrementGrainCounts(_grainTypeName);
        }

        public void OnDestroyActivation(IGrainContext grainContext)
        {
            GrainInstruments.DecrementGrainCounts(_grainTypeName);
        }
    }

    internal interface IActivationLifecycleObserver
    {
        void OnCreateActivation(IGrainContext grainContext);
        void OnDestroyActivation(IGrainContext grainContext);
    }
}
