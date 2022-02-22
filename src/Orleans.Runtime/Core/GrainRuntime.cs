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
                CheckRuntimeContext(RuntimeContext.Current);
                return this.grainFactory;
            }
        }

        public ITimerRegistry TimerRegistry
        {
            get
            {
                CheckRuntimeContext(RuntimeContext.Current);
                return this.timerRegistry;
            }
        }

        public IReminderRegistry ReminderRegistry
        {
            get
            {
                CheckRuntimeContext(RuntimeContext.Current);
                return this.reminderRegistry;
            }
        }

        public IServiceProvider ServiceProvider
        {
            get
            {
                CheckRuntimeContext(RuntimeContext.Current);
                return this.serviceProvider;
            }
        }

        public void DeactivateOnIdle(IGrainContext grainContext)
        {
            CheckRuntimeContext(grainContext);
            grainContext.Deactivate(new(DeactivationReasonCode.ApplicationRequested, $"{nameof(DeactivateOnIdle)} was called."));
        }

        public void DelayDeactivation(IGrainContext grainContext, TimeSpan timeSpan)
        {
            CheckRuntimeContext(grainContext);
            if (grainContext is not ICollectibleGrainContext collectibleContext)
            {
                throw new NotSupportedException($"Grain context {grainContext} does not implement {nameof(ICollectibleGrainContext)} and therefore {nameof(DelayDeactivation)} is not supported");
            }

            collectibleContext.DelayDeactivation(timeSpan);
        }

        public IStorage<TGrainState> GetStorage<TGrainState>(IGrainContext grainContext)
        {
            if (grainContext is null) throw new ArgumentNullException(nameof(grainContext));
            var grainType = grainContext.GrainInstance?.GetType() ?? throw new ArgumentNullException(nameof(IGrainContext.GrainInstance));
            IGrainStorage grainStorage = GrainStorageHelpers.GetGrainStorage(grainType, ServiceProvider);
            string grainTypeName = grainContext.GrainInstance.GetType().FullName;
            return new StateStorageBridge<TGrainState>(grainTypeName, grainContext.GrainReference, grainStorage, this.loggerFactory);
        }

        public static void CheckRuntimeContext(IGrainContext context)
        {
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
