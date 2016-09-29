using System;
using Orleans.Streams;
using Orleans.Timers;

namespace Orleans.Runtime
{
    internal class GrainRuntime : IGrainRuntime
    {
        public GrainRuntime(Guid serviceId, string siloId, IGrainFactory grainFactory, ITimerRegistry timerRegistry, IReminderRegistry reminderRegistry, IStreamProviderManager streamProviderManager, IServiceProvider serviceProvider)
        {
            ServiceId = serviceId;
            SiloIdentity = siloId;
            GrainFactory = grainFactory;
            TimerRegistry = timerRegistry;
            ReminderRegistry = reminderRegistry;
            StreamProviderManager = streamProviderManager;
            ServiceProvider = serviceProvider;
        }

        public Guid ServiceId { get; private set; }

        public string SiloIdentity { get; private set; }

        public IGrainFactory GrainFactory { get; private set; }
        
        public ITimerRegistry TimerRegistry { get; private set; }
        
        public IReminderRegistry ReminderRegistry { get; private set; }
        
        public IStreamProviderManager StreamProviderManager { get; private set;}
        public IServiceProvider ServiceProvider { get; private set; }

        public Logger GetLogger(string loggerName)
        {
            return LogManager.GetLogger(loggerName, LoggerType.Grain);
        }

        public void DeactivateOnIdle(Grain grain)
        {
            RuntimeClient.Current.DeactivateOnIdle(grain.Data.ActivationId);
        }

        public void DelayDeactivation(Grain grain, TimeSpan timeSpan)
        {
            grain.Data.DelayDeactivation(timeSpan);
        }
    }
}