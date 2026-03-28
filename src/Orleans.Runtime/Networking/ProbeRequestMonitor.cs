using System;
using System.Threading;

namespace Orleans.Runtime.Messaging
{
    /// <summary>
    /// Monitors incoming cluster health probe requests
    /// </summary>
    internal sealed class ProbeRequestMonitor
    {
#if NET9_0_OR_GREATER
        private readonly Lock _lock = new();
#else
        private readonly object _lock = new();
#endif
        private ValueStopwatch _probeRequestStopwatch;

        /// <summary>
        /// Called when this silo receives a health probe request.
        /// </summary>
        public void OnReceivedProbeRequest()
        {
            lock (_lock)
            {
                _probeRequestStopwatch.Restart();
            }
        }

        /// <summary>
        /// The duration which has elapsed since the most recently received health probe request.
        /// </summary>
        public TimeSpan? ElapsedSinceLastProbeRequest => _probeRequestStopwatch.IsRunning ? (Nullable<TimeSpan>)_probeRequestStopwatch.Elapsed : null;
    }
}
