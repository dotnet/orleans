using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime;

internal partial class ActivationDataActivatorProvider(
    GrainClassMap grainClassMap,
    IServiceProvider serviceProvider,
    GrainTypeSharedContextResolver sharedComponentsResolver,
    ILogger<WorkItemGroup> workItemGroupLogger,
    ILogger<ActivationTaskScheduler> activationTaskSchedulerLogger,
    IOptions<SchedulingOptions> schedulingOptions,
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
            workItemGroupLogger,
            activationTaskSchedulerLogger,
            schedulingOptions);

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
        private readonly ILogger<WorkItemGroup> _workItemGroupLogger;
        private readonly ILogger<ActivationTaskScheduler> _activationTaskSchedulerLogger;
        private readonly IOptions<SchedulingOptions> _schedulingOptions;
        private readonly IGrainActivator _grainActivator;
        private readonly IServiceProvider _serviceProvider;
        private readonly GrainTypeSharedContext _sharedComponents;
        private readonly Func<IGrainContext, WorkItemGroup> _createWorkItemGroup;
        private readonly Action<object?> _startActivation;

        public ActivationDataActivator(
            IGrainActivator grainActivator,
            IServiceProvider serviceProvider,
            GrainTypeSharedContext sharedComponents,
            ILogger<WorkItemGroup> workItemGroupLogger,
            ILogger<ActivationTaskScheduler> activationTaskSchedulerLogger,
            IOptions<SchedulingOptions> schedulingOptions)
        {
            _workItemGroupLogger = workItemGroupLogger;
            _activationTaskSchedulerLogger = activationTaskSchedulerLogger;
            _schedulingOptions = schedulingOptions;
            _grainActivator = grainActivator;
            _serviceProvider = serviceProvider;
            _sharedComponents = sharedComponents;
            _createWorkItemGroup = context => new WorkItemGroup(
                context,
                _workItemGroupLogger,
                _activationTaskSchedulerLogger,
                _schedulingOptions);
             _startActivation = state => ((ActivationData)state!).Start(_grainActivator);
        }

        public IGrainContext CreateContext(GrainAddress activationAddress)
        {
            var context = new ActivationData(
                activationAddress,
                _createWorkItemGroup,
                _serviceProvider,
                _sharedComponents);

            using var ecSuppressor = ExecutionContext.SuppressFlow();
            _ = Task.Factory.StartNew(
                _startActivation,
                context,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                context.ActivationTaskScheduler);
            return context;
        }
    }
}

internal class StatelessWorkerActivator(StatelessWorkerGrainTypeSharedContext sharedContext, IGrainContextActivator innerActivator) : IGrainContextActivator
{
    public IGrainContext CreateContext(GrainAddress address) => new StatelessWorkerGrainContext(address, sharedContext, innerActivator);
}
