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
        /// Gets a unique identifier for the current silo.
        /// There is no semantic content to this string, but it may be useful for logging.
        /// </summary>
        string SiloIdentity { get; }

        /// <summary>
        /// Gets the silo address associated with this instance.
        /// </summary>
        SiloAddress SiloAddress { get; }

        /// <summary>
        /// Gets the grain factory.
        /// </summary>
        IGrainFactory GrainFactory { get; }

        /// <summary>
        /// Gets the timer registry.
        /// </summary>
        ITimerRegistry TimerRegistry { get; }

        /// <summary>
        /// Gets the reminder registry.
        /// </summary>
        IReminderRegistry ReminderRegistry { get; }

        /// <summary>
        /// Gets the service provider.
        /// </summary>
        IServiceProvider ServiceProvider { get; }

        /// <summary>
        /// Deactivates the provided grain when it becomes idle.
        /// </summary>
        /// <param name="grainContext">The grain context.</param>
        void DeactivateOnIdle(IGrainContext grainContext);

        /// <summary>
        /// Delays idle activation collection of the provided grain due to inactivity until at least the specified time has elapsed.
        /// </summary>
        /// <param name="grainContext">The grain context.</param>
        /// <param name="timeSpan">The time to delay idle activation collection for.</param>
        void DelayDeactivation(IGrainContext grainContext, TimeSpan timeSpan);

        /// <summary>
        /// Gets grain storage for the provided grain.
        /// </summary>
        /// <typeparam name="TGrainState">The grain state type.</typeparam>
        /// <param name="grainContext">The grain context.</param>
        /// <returns>The grain storage for the provided grain.</returns>
        IStorage<TGrainState> GetStorage<TGrainState>(IGrainContext grainContext);
    }
}
