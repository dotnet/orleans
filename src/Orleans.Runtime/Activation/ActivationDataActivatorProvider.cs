using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Orleans.GrainReferences;
using Orleans.Metadata;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime
{
    internal class ActivationDataActivatorProvider : IGrainContextActivatorProvider
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IActivationWorkingSet _activationWorkingSet;
        private readonly WorkItemGroupShared _workItemGroupShared;
        private readonly GrainTypeSharedContextResolver _sharedComponentsResolver;
        private readonly GrainClassMap _grainClassMap;
        private readonly ILoggerFactory _loggerFactory;
        private readonly GrainReferenceActivator _grainReferenceActivator;

        public ActivationDataActivatorProvider(
            GrainClassMap grainClassMap,
            IServiceProvider serviceProvider,
            ILoggerFactory loggerFactory,
            GrainReferenceActivator grainReferenceActivator,
            GrainTypeSharedContextResolver sharedComponentsResolver,
            IActivationWorkingSet activationWorkingSet,
            WorkItemGroupShared workItemGroupShared)
        {
            _activationWorkingSet = activationWorkingSet;
            _workItemGroupShared = workItemGroupShared;
            _sharedComponentsResolver = sharedComponentsResolver;
            _grainClassMap = grainClassMap;
            _serviceProvider = serviceProvider;
            _loggerFactory = loggerFactory;
            _grainReferenceActivator = grainReferenceActivator;
        }

        public bool TryGet(GrainType grainType, out IGrainContextActivator activator)
        {
            if (!_grainClassMap.TryGetGrainClass(grainType, out var grainClass) || !typeof(IGrain).IsAssignableFrom(grainClass))
            {
                activator = null;
                return false;
            }

            var sharedContext = _sharedComponentsResolver.GetComponents(grainType);
            var instanceActivator = sharedContext.GetComponent<IGrainActivator>();
            if (instanceActivator is null)
            {
                throw new InvalidOperationException($"Could not find a suitable {nameof(IGrainActivator)} implementation for grain type {grainType}");
            }

            var innerActivator = new ActivationDataActivator(
                instanceActivator,
                _serviceProvider,
                sharedContext,
                _workItemGroupShared);

            if (sharedContext.PlacementStrategy is StatelessWorkerPlacement)
            {
                activator = new StatelessWorkerActivator(sharedContext, innerActivator);
            }
            else
            {
                activator = innerActivator;
            }

            return true;
        }

        private class ActivationDataActivator : IGrainContextActivator
        {
            private readonly WorkItemGroupShared _workItemGroupShared;
            private readonly IGrainActivator _grainActivator;
            private readonly IServiceProvider _serviceProvider;
            private readonly GrainTypeSharedContext _sharedComponents;

            public ActivationDataActivator(
                IGrainActivator grainActivator,
                IServiceProvider serviceProvider,
                GrainTypeSharedContext sharedComponents,
                WorkItemGroupShared workItemGroupShared)
            {
                _workItemGroupShared = workItemGroupShared;
                _grainActivator = grainActivator;
                _serviceProvider = serviceProvider;
                _sharedComponents = sharedComponents;
            }

            public IGrainContext CreateContext(GrainAddress activationAddress)
            {
                var context = new ActivationData(
                    activationAddress,
                    _workItemGroupShared,
                    _serviceProvider,
                    _sharedComponents);

                var previousContext = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(context.SchedulingContext);

                try
                {
                    // Instantiate the grain itself
                    var instance = _grainActivator.CreateInstance(context);
                    context.SetGrainInstance(instance);
                }
                finally
                {
                    if (previousContext is not null)
                    {
                        SynchronizationContext.SetSynchronizationContext(previousContext);
                    }
                }

                return context;
            }
        }
    }

    internal class StatelessWorkerActivator : IGrainContextActivator
    {
        private readonly IGrainContextActivator _innerActivator;
        private readonly GrainTypeSharedContext _sharedContext;

        public StatelessWorkerActivator(GrainTypeSharedContext sharedContext, IGrainContextActivator innerActivator)
        {
            _innerActivator = innerActivator;
            _sharedContext = sharedContext;
        }

        public IGrainContext CreateContext(GrainAddress address) => new StatelessWorkerGrainContext(address, _sharedContext, _innerActivator);
    }
}