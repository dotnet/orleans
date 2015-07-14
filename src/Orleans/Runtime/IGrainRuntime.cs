using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Orleans.Core;
using Orleans.Streams;
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

        IGrainFactory GrainFactory { get; }

        ITimerRegistry TimerRegistry { get; }

        IReminderRegistry ReminderRegistry { get; }

        IStreamProviderManager StreamProviderManager { get; }

        Logger GetLogger(string loggerName, TraceLogger.LoggerType logType);

        void DeactivateOnIdle(Grain grain);

        void DelayDeactivation(Grain grain, TimeSpan timeSpan);
    }
}
