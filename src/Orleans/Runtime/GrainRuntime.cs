using System;
using Orleans.Core;
using Orleans.Streams;
using Orleans.Timers;

namespace Orleans.Runtime
{
    internal class GrainRuntime : IGrainRuntime
    {
        public GrainRuntime(string siloId, IGrainFactory grainFactory, ITimerRegistry timerRegistry, IReminderRegistry reminderRegistry, IStreamProviderManager streamProviderManager)
        {
            SiloIdentity = siloId;
            GrainFactory = grainFactory;
            TimerRegistry = timerRegistry;
            ReminderRegistry = reminderRegistry;
            StreamProviderManager = streamProviderManager;
        }

        public string SiloIdentity { get; private set; }

        public IGrainFactory GrainFactory { get; private set; }
        
        public ITimerRegistry TimerRegistry { get; private set; }
        
        public IReminderRegistry ReminderRegistry { get; private set; }
        
        public IStreamProviderManager StreamProviderManager { get; private set;}

        public Logger GetLogger(string loggerName, TraceLogger.LoggerType logType)
        {
            return TraceLogger.GetLogger(loggerName, logType);
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