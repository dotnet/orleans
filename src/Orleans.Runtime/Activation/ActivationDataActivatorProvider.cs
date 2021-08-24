using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainReferences;
using Orleans.Metadata;
using Orleans.Runtime.Placement;

namespace Orleans.Runtime
{
    internal class ActivationDataActivatorProvider : IGrainContextActivatorProvider
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly PlacementStrategyResolver _placementStrategyResolver;
        private readonly IActivationCollector _activationCollector;
        private readonly GrainManifest _siloManifest;
        private readonly IActivationWorkingSet _activationWorkingSet;
        private readonly GrainTypeComponentsResolver _sharedComponentsResolver;
        private readonly GrainClassMap _grainClassMap;
        private readonly GrainCollectionOptions _collectionOptions;
        private readonly IOptions<SiloMessagingOptions> _messagingOptions;
        private readonly TimeSpan _maxWarningRequestProcessingTime;
        private readonly TimeSpan _maxRequestProcessingTime;
        private readonly ILoggerFactory _loggerFactory;
        private readonly GrainReferenceActivator _grainReferenceActivator;
        private ActivationMessageScheduler _activationMessageScheduler;
        private IGrainRuntime _grainRuntime;

        public ActivationDataActivatorProvider(
            GrainClassMap grainClassMap,
            IServiceProvider serviceProvider,
            PlacementStrategyResolver placementStrategyResolver,
            IActivationCollector activationCollector,
            IClusterManifestProvider clusterManifestProvider,
            IOptions<SiloMessagingOptions> messagingOptions,
            IOptions<GrainCollectionOptions> collectionOptions,
            ILoggerFactory loggerFactory,
            GrainReferenceActivator grainReferenceActivator,
            GrainTypeComponentsResolver sharedComponentsResolver,
            IActivationWorkingSet activationWorkingSet)
        {
            _activationWorkingSet = activationWorkingSet;
            _sharedComponentsResolver = sharedComponentsResolver;
            _grainClassMap = grainClassMap;
            _serviceProvider = serviceProvider;
            _placementStrategyResolver = placementStrategyResolver;
            _activationCollector = activationCollector;
            _siloManifest = clusterManifestProvider.LocalGrainManifest;
            _collectionOptions = collectionOptions.Value;
            _messagingOptions = messagingOptions;
            _maxWarningRequestProcessingTime = messagingOptions.Value.ResponseTimeout.Multiply(5);
            _maxRequestProcessingTime = messagingOptions.Value.MaxRequestProcessingTime;
            _loggerFactory = loggerFactory;
            _grainReferenceActivator = grainReferenceActivator;
        }

        public bool TryGet(GrainType grainType, out IGrainContextActivator activator)
        {
            if (!_grainClassMap.TryGetGrainClass(grainType, out var grainClass)
                || !typeof(Grain).IsAssignableFrom(grainClass))
            {
                activator = null;
                return false;
            }

            var sharedComponents = _sharedComponentsResolver.GetComponents(grainType);
            IGrainActivator instanceActivator = sharedComponents.GetComponent<IGrainActivator>();
            if (instanceActivator is null)
            {
                throw new InvalidOperationException($"Could not find a suitable {nameof(IGrainActivator)} implementation for grain type {grainType}");
            }

            var collectionAgeLimit = GetCollectionAgeLimit(grainType, grainClass);

            activator = new ActivationDataActivator(
                instanceActivator,
                _placementStrategyResolver.GetPlacementStrategy(grainType),
                collectionAgeLimit,
                _messagingOptions,
                _maxWarningRequestProcessingTime,
                _maxRequestProcessingTime,
                _loggerFactory,
                _serviceProvider,
                _grainRuntime ??= _serviceProvider.GetRequiredService<IGrainRuntime>(),
                _grainReferenceActivator,
                sharedComponents,
                _activationMessageScheduler ??= _serviceProvider.GetRequiredService<ActivationMessageScheduler>(),
                _activationWorkingSet);
            return true;
        }

        private TimeSpan GetCollectionAgeLimit(GrainType grainType, Type grainClass)
        {
            if (_siloManifest.Grains.TryGetValue(grainType, out var properties)
                && properties.Properties.TryGetValue(WellKnownGrainTypeProperties.IdleDeactivationPeriod, out var idleTimeoutString))
            {
                if (string.Equals(idleTimeoutString, WellKnownGrainTypeProperties.IndefiniteIdleDeactivationPeriodValue))
                {
                    return default;
                }

                if (TimeSpan.TryParse(idleTimeoutString, out var result))
                {
                    return result;
                }
            }

            if (_collectionOptions.ClassSpecificCollectionAge.TryGetValue(grainClass.FullName, out var specified))
            {
                return specified;
            }

            return _collectionOptions.CollectionAge;
        }

        private class ActivationDataActivator : IGrainContextActivator
        {
            private readonly IActivationWorkingSet _activationWorkingSet;
            private readonly IGrainActivator _grainActivator;
            private readonly PlacementStrategy _placementStrategy;
            private readonly TimeSpan _collectionAgeLimit;
            private readonly IOptions<SiloMessagingOptions> _messagingOptions;
            private readonly TimeSpan _maxWarningRequestProcessingTime;
            private readonly TimeSpan _maxRequestProcessingTime;
            private readonly ILoggerFactory _loggerFactory;
            private readonly IServiceProvider _serviceProvider;
            private readonly IGrainRuntime _grainRuntime;
            private readonly GrainReferenceActivator _grainReferenceActivator;
            private readonly GrainTypeComponents _sharedComponents;
            private readonly ActivationMessageScheduler _activationMessageScheduler;

            public ActivationDataActivator(
                IGrainActivator grainActivator,
                PlacementStrategy placementStrategy,
                TimeSpan collectionAgeLimit,
                IOptions<SiloMessagingOptions> messagingOptions,
                TimeSpan maxWarningRequestProcessingTime,
                TimeSpan maxRequestProcessingTime,
                ILoggerFactory loggerFactory,
                IServiceProvider serviceProvider,
                IGrainRuntime grainRuntime,
                GrainReferenceActivator grainReferenceActivator,
                GrainTypeComponents sharedComponents,
                ActivationMessageScheduler activationMessageScheduler,
                IActivationWorkingSet activationWorkingSet)
            {
                _activationWorkingSet = activationWorkingSet;
                _grainActivator = grainActivator;
                _placementStrategy = placementStrategy;
                _collectionAgeLimit = collectionAgeLimit;
                _messagingOptions = messagingOptions;
                _maxWarningRequestProcessingTime = maxWarningRequestProcessingTime;
                _maxRequestProcessingTime = maxRequestProcessingTime;
                _loggerFactory = loggerFactory;
                _serviceProvider = serviceProvider;
                _grainRuntime = grainRuntime;
                _grainReferenceActivator = grainReferenceActivator;
                _sharedComponents = sharedComponents;
                _activationMessageScheduler = activationMessageScheduler;
            }

            public IGrainContext CreateContext(ActivationAddress activationAddress)
            {
                var context = new ActivationData(
                    activationAddress,
                    _placementStrategy,
                    _collectionAgeLimit,
                    _messagingOptions,
                    _maxWarningRequestProcessingTime,
                    _maxRequestProcessingTime,
                    _loggerFactory,
                    _serviceProvider,
                    _grainRuntime,
                    _grainReferenceActivator,
                    _sharedComponents,
                    _activationMessageScheduler,
                    _activationWorkingSet);

                RuntimeContext.SetExecutionContext(context, out var existingContext);

                try
                {
                    // Instantiate the grain itself
                    var grainInstance = (Grain)_grainActivator.CreateInstance(context);
                    context.SetGrainInstance(grainInstance);

                    (grainInstance as ILifecycleParticipant<IGrainLifecycle>)?.Participate(context.ObservableLifecycle);
                }
                finally
                {
                    RuntimeContext.SetExecutionContext(existingContext);
                }

                return context;
            }
        }
    }

}