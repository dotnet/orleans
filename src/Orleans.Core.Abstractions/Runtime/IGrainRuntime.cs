using System;
using Orleans.Core;
using Orleans.Timers;

namespace Orleans.Runtime
{
    /// <summary>
    /// The gateway of the <see cref="Grain"/> to the Orleans runtime. The <see cref="Grain"/> should only interact with the runtime through this interface.
    /// </summary>
    public interface IGrainRuntime
    {
        /// <summary>
        /// Provides the ServiceId this cluster is running as.
        /// ServiceId's are intended to be long lived Id values for a particular service which will remain constant 
        /// even if the service is started / redeployed multiple times during its operations life.
        /// </summary>
        /// <returns>ServiceId Guid for this service.</returns>
        string ServiceId { get; }

        /// <summary>
        /// A unique identifier for the current silo.
        /// There is no semantic content to this string, but it may be useful for logging.
        /// </summary>
        string SiloIdentity { get; }

        SiloAddress SiloAddress { get; }

        IGrainFactory GrainFactory { get; }

        ITimerRegistry TimerRegistry { get; }

        IReminderRegistry ReminderRegistry { get; }

        IServiceProvider ServiceProvider { get; }

        void DeactivateOnIdle(Grain grain);

        void DelayDeactivation(Grain grain, TimeSpan timeSpan);

        IStorage<TGrainState> GetStorage<TGrainState>(Grain grain);
    }
}
