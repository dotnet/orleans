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
    public SiloAddress SiloAddress => localSiloDetails.SiloAddress;

    public ILoggerFactory LoggerFactory => loggerFactory;
    internal ILogger SchedulerLogger { get; } = loggerFactory.CreateLogger<WorkItemGroup>();
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
            schedulingOptions);
    }
}
