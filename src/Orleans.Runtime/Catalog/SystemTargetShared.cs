#nullable enable
using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainReferences;
using Orleans.Runtime.Scheduler;
using Orleans.Timers;

namespace Orleans.Runtime;

internal sealed class SystemTargetShared(
    InsideRuntimeClient runtimeClient,
    ILocalSiloDetails localSiloDetails,
    ILoggerFactory loggerFactory,
    IOptions<SchedulingOptions> schedulingOptions,
    GrainReferenceActivator grainReferenceActivator,
    ITimerRegistry timerRegistry,
    ActivationDirectory activations)
{
    private readonly ILogger<WorkItemGroup> _workItemGroupLogger = loggerFactory.CreateLogger<WorkItemGroup>();
    private readonly ILogger<ActivationTaskScheduler> _activationTaskSchedulerLogger = loggerFactory.CreateLogger<ActivationTaskScheduler>();
    public SiloAddress SiloAddress => localSiloDetails.SiloAddress;

    public ILoggerFactory LoggerFactory => loggerFactory;
    public GrainReferenceActivator GrainReferenceActivator => grainReferenceActivator;
    public ITimerRegistry TimerRegistry => timerRegistry;

    public RuntimeMessagingTrace MessagingTrace { get; } = new(loggerFactory);
    public InsideRuntimeClient RuntimeClient => runtimeClient;
    public ActivationDirectory ActivationDirectory => activations;
    public WorkItemGroup CreateWorkItemGroup(SystemTarget systemTarget)
    {
        ArgumentNullException.ThrowIfNull(systemTarget);
        return new WorkItemGroup(
            systemTarget,
            _workItemGroupLogger,
            _activationTaskSchedulerLogger,
            schedulingOptions);
    }
}
