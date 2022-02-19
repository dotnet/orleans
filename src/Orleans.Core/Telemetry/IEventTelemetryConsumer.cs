using System.Collections.Generic;

namespace Orleans.Runtime
{
    /// <summary>
    /// Trace consumer which tracks events.
    /// Implements the <see cref="Orleans.Runtime.ITelemetryConsumer" />
    /// </summary>
    /// <seealso cref="Orleans.Runtime.ITelemetryConsumer" />
    public interface IEventTelemetryConsumer : ITelemetryConsumer
    {
        /// <summary>
        /// Tracks an event.
        /// </summary>
        /// <param name="eventName">Name of the event.</param>
        /// <param name="properties">The properties.</param>
        /// <param name="metrics">The metrics.</param>
        void TrackEvent(string eventName, IDictionary<string, string> properties = null, IDictionary<string, double> metrics = null);
    }
}
