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
