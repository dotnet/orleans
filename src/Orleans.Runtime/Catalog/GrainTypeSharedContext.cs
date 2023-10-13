#nullable enable
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
using Orleans.Serialization.Session;
using Orleans.Serialization.TypeSystem;

namespace Orleans.Runtime
{
    /// <summary>
    /// Functionality which is shared between all instances of a grain type.
    /// </summary>
    public class GrainTypeSharedContext
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly Dictionary<Type, object> _components = new();
        private InternalGrainRuntime? _internalGrainRuntime;

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
            IServiceProvider serviceProvider,
            SerializerSessionPool serializerSessionPool)
        {
            if (!grainClassMap.TryGetGrainClass(grainType, out var grainClass))
            {
                throw new KeyNotFoundException($"Could not find corresponding grain class for grain of type {grainType.ToString()}");
            }

            SerializerSessionPool = serializerSessionPool;
            GrainTypeName = RuntimeTypeNameFormatter.Format(grainClass);
            Logger = logger;
            MessagingOptions = messagingOptions.Value;
            GrainReferenceActivator = grainReferenceActivator;
            _serviceProvider = serviceProvider;
            MaxWarningRequestProcessingTime = messagingOptions.Value.ResponseTimeout.Multiply(5);
            MaxRequestProcessingTime = messagingOptions.Value.MaxRequestProcessingTime;
            PlacementStrategy = placementStrategyResolver.GetPlacementStrategy(grainType);
            SchedulingOptions = schedulingOptions.Value;
            Runtime = grainRuntime;
            MigrationManager = _serviceProvider.GetService<IActivationMigrationManager>();

            CollectionAgeLimit = GetCollectionAgeLimit(
                grainType,
                grainClass,
                clusterManifestProvider.LocalGrainManifest,
                collectionOptions.Value);
        }

        /// <summary>
        /// Gets the grain instance type name, if available.
        /// </summary>
        public string? GrainTypeName { get; }

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

            if (collectionOptions.ClassSpecificCollectionAge.TryGetValue(grainClass.FullName!, out var specified))
            {
                return specified;
            }

            return collectionOptions.CollectionAge;
        }

        /// <summary>
        /// Gets a component.
        /// </summary>
        /// <typeparam name="TComponent">The type specified in the corresponding <see cref="SetComponent{TComponent}"/> call.</typeparam>
        public TComponent? GetComponent<TComponent>()
        {
            if (typeof(TComponent) == typeof(PlacementStrategy) && PlacementStrategy is TComponent component)
            {
                return component;
            }

            if (_components is null) return default;
            _components.TryGetValue(typeof(TComponent), out var resultObj);
            return (TComponent?)resultObj;
        }

        /// <summary>
        /// Registers a component.
        /// </summary>
        /// <typeparam name="TComponent">The type which can be used as a key to <see cref="GetComponent{TComponent}"/>.</typeparam>
        public void SetComponent<TComponent>(TComponent? instance)
        {
            if (instance == null)
            {
                _components.Remove(typeof(TComponent));
                return;
            }

            _components[typeof(TComponent)] = instance;
        }

        /// <summary>
        /// Gets the duration after which idle grains are eligible for collection.
        /// </summary>
        public TimeSpan CollectionAgeLimit { get; }

        /// <summary>
        /// Gets the logger.
        /// </summary>
        public ILogger Logger { get; }

        /// <summary>
        /// Gets the serializer session pool.
        /// </summary>
        public SerializerSessionPool SerializerSessionPool { get; }

        /// <summary>
        /// Gets the silo messaging options.
        /// </summary>
        public SiloMessagingOptions MessagingOptions { get; }

        /// <summary>
        /// Gets the grain reference activator.
        /// </summary>
        public GrainReferenceActivator GrainReferenceActivator { get; }

        /// <summary>
        /// Gets the maximum amount of time we expect a request to continue processing before it is considered hung.
        /// </summary>
        public TimeSpan MaxRequestProcessingTime { get; }

        /// <summary>
        /// Gets the maximum amount of time we expect a request to continue processing before a warning may be logged.
        /// </summary>
        public TimeSpan MaxWarningRequestProcessingTime { get; }

        /// <summary>
        /// Gets the placement strategy used by grains of this type.
        /// </summary>
        public PlacementStrategy PlacementStrategy { get; }

        /// <summary>
        /// Gets the scheduling options.
        /// </summary>
        public SchedulingOptions SchedulingOptions { get; }

        /// <summary>
        /// Gets the grain runtime.
        /// </summary>
        public IGrainRuntime Runtime { get; }

        /// <summary>
        /// Gets the local activation migration manager.
        /// </summary>
        internal IActivationMigrationManager? MigrationManager { get; }

        /// <summary>
        /// Gets the internal grain runtime.
        /// </summary>
        internal InternalGrainRuntime InternalRuntime => _internalGrainRuntime ??= _serviceProvider.GetRequiredService<InternalGrainRuntime>();

        /// <summary>
        /// Called on creation of an activation.
        /// </summary>
        /// <param name="grainContext">The grain activation.</param>
        public void OnCreateActivation(IGrainContext grainContext)
        {
            GrainInstruments.IncrementGrainCounts(GrainTypeName);
        }

        /// <summary>
        /// Called when an activation is disposed.
        /// </summary>
        /// <param name="grainContext">The grain activation.</param>
        public void OnDestroyActivation(IGrainContext grainContext)
        {
            GrainInstruments.DecrementGrainCounts(GrainTypeName);
        }
    }

    internal interface IActivationLifecycleObserver
    {
        void OnCreateActivation(IGrainContext grainContext);
        void OnDestroyActivation(IGrainContext grainContext);
    }
}
