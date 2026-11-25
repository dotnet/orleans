using System;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime.Messaging
{
    /// <summary>
    /// Monitors incoming cluster health probe requests
    /// </summary>
    internal sealed class ProbeRequestMonitor
    {
        private readonly Lock _lock = new();
        private readonly TimeProvider _timeProvider;
        private long? _lastProbeRequestTimestamp;

        public ProbeRequestMonitor([FromKeyedServices(TimeProviderNames.Membership)] TimeProvider timeProvider)
        {
            _timeProvider = timeProvider;
        }

        /// <summary>
        /// Called when this silo receives a health probe request.
        /// </summary>
        public void OnReceivedProbeRequest()
        {
            lock (_lock)
            {
                _lastProbeRequestTimestamp = _timeProvider.GetTimestamp();
            }
        }

        /// <summary>
        /// The duration which has elapsed since the most recently received health probe request.
        /// </summary>
        public TimeSpan? ElapsedSinceLastProbeRequest
        {
            get
            {
                lock (_lock)
                {
                    return _lastProbeRequestTimestamp is { } timestamp ? _timeProvider.GetElapsedTime(timestamp) : null;
                }
            }
        }
    }
}
