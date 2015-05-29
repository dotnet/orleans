using System;
using Orleans.Core;
using Orleans.Streams;
using Orleans.Timers;

namespace Orleans.Runtime
{
    internal class GrainRuntime : IGrainRuntime
    {
        private readonly string id;

        public GrainRuntime(string id, IGrainFactory grainFactory, ITimerRegistry timerRegistry, IReminderRegistry reminderRegistry, IStreamProviderManager streamProviderManager, IStorage storage=null)
        {
            this.id = id;
            GrainFactory = grainFactory;
            TimerRegistry = timerRegistry;
            ReminderRegistry = reminderRegistry;
            StreamProviderManager = streamProviderManager;
            Storage = storage;
        }

        public string SiloIdentity
        {
            get { return id; }
        }

        public IGrainFactory GrainFactory { get; private set; }
        
        public ITimerRegistry TimerRegistry { get; private set; }
        
        public IReminderRegistry ReminderRegistry { get; private set; }
        
        public IStreamProviderManager StreamProviderManager { get; private set;}

        public IStorage Storage { get; private set; }
        
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