using System;
using Microsoft.Extensions.Logging;
using Orleans.Core;
using Orleans.Timers;
using Orleans.Storage;

namespace Orleans.Runtime
{
    internal class GrainRuntime : IGrainRuntime
    {
        private readonly ILoggerFactory loggerFactory;
        private readonly IServiceProvider serviceProvider;
        private readonly IReminderRegistry reminderRegistry;
        private readonly ITimerRegistry timerRegistry;
        private readonly IGrainFactory grainFactory;

        public GrainRuntime(
            ILocalSiloDetails localSiloDetails,
            IGrainFactory grainFactory,
            ITimerRegistry timerRegistry,
            IReminderRegistry reminderRegistry,
            IServiceProvider serviceProvider,
            ILoggerFactory loggerFactory)
        {
            SiloAddress = localSiloDetails.SiloAddress;
            SiloIdentity = SiloAddress.ToLongString();
            this.grainFactory = grainFactory;
            this.timerRegistry = timerRegistry;
            this.reminderRegistry = reminderRegistry;
            this.serviceProvider = serviceProvider;
            this.loggerFactory = loggerFactory;
        }

        public string SiloIdentity { get; }

        public SiloAddress SiloAddress { get; }

        public IGrainFactory GrainFactory
        {
            get
            {
                CheckRuntimeContext();
                return this.grainFactory;
            }
        }

        public ITimerRegistry TimerRegistry
        {
            get
            {
                CheckRuntimeContext();
                return this.timerRegistry;
            }
        }

        public IReminderRegistry ReminderRegistry
        {
            get
            {
                CheckRuntimeContext();
                return this.reminderRegistry;
            }
        }

        public IServiceProvider ServiceProvider
        {
            get
            {
                CheckRuntimeContext();
                return this.serviceProvider;
            }
        }

        public void DeactivateOnIdle(Grain grain)
        {
            CheckRuntimeContext();
            grain.Data.Deactivate();
        }

        public void DelayDeactivation(Grain grain, TimeSpan timeSpan)
        {
            CheckRuntimeContext();
            grain.Data.DelayDeactivation(timeSpan);
        }

        public IStorage<TGrainState> GetStorage<TGrainState>(Grain grain)
        {
            IGrainStorage grainStorage = grain.GetGrainStorage(ServiceProvider);
            string grainTypeName = grain.GetType().FullName;
            return new StateStorageBridge<TGrainState>(grainTypeName, grain.GrainReference, grainStorage, this.loggerFactory);
        }

        public static void CheckRuntimeContext()
        {
            var context = RuntimeContext.Current;

            if (context is null)
            {
                // Move exceptions into local functions to help inlining this method.
                ThrowMissingContext();
                void ThrowMissingContext() => throw new InvalidOperationException("Activation access violation. A non-activation thread attempted to access activation services.");
            }

            if (context is ActivationData activation
                && (activation.State == ActivationState.Invalid || activation.State == ActivationState.FailedToActivate))
            {
                // Move exceptions into local functions to help inlining this method.
                ThrowInvalidActivation(activation);
                void ThrowInvalidActivation(ActivationData activationData) => throw new InvalidOperationException($"Attempt to access an invalid activation: {activationData}");
            }
        }
    }
}
