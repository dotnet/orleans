using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime;

internal partial class ActivationDataActivatorProvider(
    GrainClassMap grainClassMap,
    IServiceProvider serviceProvider,
    GrainTypeSharedContextResolver sharedComponentsResolver,
    IOptions<SchedulingOptions> schedulingOptions,
    SchedulerInstruments schedulerInstruments,
    IOptions<StatelessWorkerOptions> statelessWorkerOptions) : IGrainContextActivatorProvider
{
    public bool TryGet(GrainType grainType, [NotNullWhen(true)] out IGrainContextActivator? activator)
    {
        if (!grainClassMap.TryGetGrainClass(grainType, out var grainClass) || !typeof(IGrain).IsAssignableFrom(grainClass))
        {
            activator = null;
            return false;
        }

        var sharedContext = sharedComponentsResolver.GetComponents(grainType);
        var instanceActivator = sharedContext.GetComponent<IGrainActivator>();
        if (instanceActivator is null)
        {
            throw new InvalidOperationException($"Could not find a suitable {nameof(IGrainActivator)} implementation for grain type {grainType}");
        }

        var innerActivator = new ActivationDataActivator(
            instanceActivator,
            serviceProvider,
            sharedContext,
            schedulingOptions,
            schedulerInstruments);

        if (sharedContext.PlacementStrategy is StatelessWorkerPlacement)
        {
            var statelessWorkerSharedContext = new StatelessWorkerGrainTypeSharedContext(sharedContext, statelessWorkerOptions);
            activator = new StatelessWorkerActivator(statelessWorkerSharedContext, innerActivator);
        }
        else
        {
            activator = innerActivator;
        }

        return true;
    }

    private partial class ActivationDataActivator : IGrainContextActivator
    {
        private readonly IOptions<SchedulingOptions> _schedulingOptions;
        private readonly IGrainActivator _grainActivator;
        private readonly IServiceProvider _serviceProvider;
        private readonly GrainTypeSharedContext _sharedComponents;
        private readonly Func<IGrainContext, WorkItemGroup> _createWorkItemGroup;
        private readonly SendOrPostCallback _startActivation;

        public ActivationDataActivator(
            IGrainActivator grainActivator,
            IServiceProvider serviceProvider,
            GrainTypeSharedContext sharedComponents,
            IOptions<SchedulingOptions> schedulingOptions,
            SchedulerInstruments schedulerInstruments)
        {
            _schedulingOptions = schedulingOptions;
            _grainActivator = grainActivator;
            _serviceProvider = serviceProvider;
            _sharedComponents = sharedComponents;
            _createWorkItemGroup = context => new WorkItemGroup(
                context,
                _schedulingOptions,
                schedulerInstruments);
            _startActivation = state => ((ActivationData)state!).Start(_grainActivator);
        }

        public IGrainContext CreateContext(GrainAddress activationAddress, IConfigureGrainContext[] configureActions)
        {
            var context = new ActivationData(
                activationAddress,
                _createWorkItemGroup,
                _serviceProvider,
                _sharedComponents);

            foreach (var configure in configureActions)
            {
                configure.Configure(context);
            }

            using var ecSuppressor = ExecutionContext.SuppressFlow();
            context.WorkItemGroup.Post(_startActivation, context);
            return context;
        }
    }
}

internal class StatelessWorkerActivator(StatelessWorkerGrainTypeSharedContext sharedContext, IGrainContextActivator innerActivator) : IGrainContextActivator
{
    public IGrainContext CreateContext(GrainAddress address, IConfigureGrainContext[] configureActions)
        => new StatelessWorkerGrainContext(address, sharedContext, innerActivator, configureActions);
}
