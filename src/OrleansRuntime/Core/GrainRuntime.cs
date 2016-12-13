using System;
using Orleans.Runtime.Configuration;
using Orleans.Streams;
using Orleans.Timers;

namespace Orleans.Runtime
{
    internal class GrainRuntime : IGrainRuntime
    {
        private readonly ISiloRuntimeClient runtimeClient;

        public GrainRuntime(
            GlobalConfiguration globalConfig,
            ILocalSiloDetails localSiloDetails,
            IGrainFactory grainFactory,
            ITimerRegistry timerRegistry,
            IReminderRegistry reminderRegistry,
            IStreamProviderManager streamProviderManager,
            IServiceProvider serviceProvider,
            ISiloRuntimeClient runtimeClient)
        {
            this.runtimeClient = runtimeClient;
            ServiceId = globalConfig.ServiceId;
            SiloIdentity = localSiloDetails.SiloAddress.ToLongString();
            GrainFactory = grainFactory;
            TimerRegistry = timerRegistry;
            ReminderRegistry = reminderRegistry;
            StreamProviderManager = streamProviderManager;
            ServiceProvider = serviceProvider;
        }

        public Guid ServiceId { get; }

        public string SiloIdentity { get; }

        public IGrainFactory GrainFactory { get; }
        
        public ITimerRegistry TimerRegistry { get; }
        
        public IReminderRegistry ReminderRegistry { get; }
        
        public IStreamProviderManager StreamProviderManager { get; }

        public IServiceProvider ServiceProvider { get; }

        public Logger GetLogger(string loggerName)
        {
            return LogManager.GetLogger(loggerName, LoggerType.Grain);
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