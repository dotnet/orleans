using System;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Configuration;
using Orleans.Streams;
using Orleans.Timers;

namespace Orleans.Runtime
{
    internal class GrainRuntime : IGrainRuntime
    {
        private readonly ISiloRuntimeClient runtimeClient;
        private readonly ILoggerFactory loggerFactory;
        public GrainRuntime(
            GlobalConfiguration globalConfig,
            ILocalSiloDetails localSiloDetails,
            IGrainFactory grainFactory,
            ITimerRegistry timerRegistry,
            IReminderRegistry reminderRegistry,
            IStreamProviderManager streamProviderManager,
            IServiceProvider serviceProvider,
            ISiloRuntimeClient runtimeClient,
            ILoggerFactory loggerFactory)
        {
            this.runtimeClient = runtimeClient;
            ServiceId = globalConfig.ServiceId;
            SiloAddress = localSiloDetails.SiloAddress;
            SiloIdentity = SiloAddress.ToLongString();
            GrainFactory = grainFactory;
            TimerRegistry = timerRegistry;
            ReminderRegistry = reminderRegistry;
            StreamProviderManager = streamProviderManager;
            ServiceProvider = serviceProvider;
            this.loggerFactory = loggerFactory;
        }

        public Guid ServiceId { get; }

        public string SiloIdentity { get; }

        public SiloAddress SiloAddress { get; }

        public IGrainFactory GrainFactory { get; }
        
        public ITimerRegistry TimerRegistry { get; }
        
        public IReminderRegistry ReminderRegistry { get; }
        
        public IStreamProviderManager StreamProviderManager { get; }

        public IServiceProvider ServiceProvider { get; }

        public Logger GetLogger(string loggerName)
        {
            return new LoggerWrapper(loggerName, this.loggerFactory);
        }

        public void DeactivateOnIdle(Grain grain)
        {
            this.runtimeClient.DeactivateOnIdle(grain.Data.ActivationId);
        }

        public void DelayDeactivation(Grain grain, TimeSpan timeSpan)
        {
            grain.Data.DelayDeactivation(timeSpan);
        }
    }
}