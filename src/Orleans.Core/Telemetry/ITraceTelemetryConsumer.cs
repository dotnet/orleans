using System.Collections.Generic;

namespace Orleans.Runtime
{
    /// <summary>
    /// Telemetry consumer which tracks messages.
    /// Implements the <see cref="Orleans.Runtime.ITelemetryConsumer" />
    /// </summary>
    /// <seealso cref="Orleans.Runtime.ITelemetryConsumer" />
    public interface ITraceTelemetryConsumer : ITelemetryConsumer
    {
        /// <summary>
        /// Tracks a trace message.
        /// </summary>
        /// <param name="message">The message.</param>
        void TrackTrace(string message);
        /// <summary>
        /// Tracks a trace message.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="severity">The severity.</param>
        void TrackTrace(string message, Severity severity);

        /// <summary>
        /// Tracks a trace message.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="severity">The severity.</param>
        /// <param name="properties">The properties.</param>
        void TrackTrace(string message, Severity severity, IDictionary<string, string> properties);

        /// <summary>
        /// Tracks a trace message.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="properties">The properties.</param>
        void TrackTrace(string message, IDictionary<string, string> properties);
    }
}
