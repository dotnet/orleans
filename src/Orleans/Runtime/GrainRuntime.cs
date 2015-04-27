using System;
using Orleans.Core;
using Orleans.Streams;
using Orleans.Timers;

namespace Orleans.Runtime
{
    internal class GrainRuntime : IGrainRuntime
    {
        private readonly string id;
        private readonly IGrainFactory grainFactory;
        private readonly ITimerRegistry timerRegistry;
        private readonly IReminderRegistry reminderRegistry;
        private readonly IStreamProviderManager streamProviderManager;

        public GrainRuntime(string id, IGrainFactory grainFactory, ITimerRegistry timerRegistry, IReminderRegistry reminderRegistry, IStreamProviderManager streamProviderManager)
        {
            this.id = id;
            this.grainFactory = grainFactory;
            this.timerRegistry = timerRegistry;
            this.reminderRegistry = reminderRegistry;
            this.streamProviderManager = streamProviderManager;
        }

        public string SiloIdentity
        {
            get { return id; }
        }

        public IGrainFactory GrainFactory
        {
            get { return grainFactory; }
        }

        public ITimerRegistry TimerRegistry
        {
            get { return timerRegistry; }
        }

        public IReminderRegistry ReminderRegistry
        {
            get { return reminderRegistry; }
        }

        public IStreamProviderManager StreamProviderManager
        {
            get { return streamProviderManager; }
        }

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