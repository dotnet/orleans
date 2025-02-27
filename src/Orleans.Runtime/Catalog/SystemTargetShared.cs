using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainReferences;
using Orleans.Runtime.Scheduler;
using Orleans.Timers;

namespace Orleans.Runtime
{
    internal sealed class SystemTargetShared(
        InsideRuntimeClient runtimeClient,
        ILocalSiloDetails localSiloDetails,
        ILoggerFactory loggerFactory,
        ILogger<WorkItemGroup> workItemGroupLogger,
        ILogger<ActivationTaskScheduler> activationTaskSchedulerLogger,
        IOptions<SchedulingOptions> schedulingOptions,
        GrainReferenceActivator grainReferenceActivator,
        RuntimeMessagingTrace messagingTrace,
        ITimerRegistry timerRegistry,
        ActivationDirectory activations)
    {
        public SiloAddress SiloAddress => localSiloDetails.SiloAddress;

        public ILoggerFactory LoggerFactory => loggerFactory;
        public GrainReferenceActivator GrainReferenceActivator => grainReferenceActivator;
        public ITimerRegistry TimerRegistry => timerRegistry;

        public RuntimeMessagingTrace MessagingTrace => messagingTrace;
        public InsideRuntimeClient RuntimeClient => runtimeClient;
        public ActivationDirectory ActivationDirectory => activations;
        public WorkItemGroup CreateWorkItemGroup(SystemTarget systemTarget)
        {
            ArgumentNullException.ThrowIfNull(systemTarget);
            return new WorkItemGroup(
                systemTarget,
                workItemGroupLogger,
                activationTaskSchedulerLogger,
                schedulingOptions);
        }
    }
}
