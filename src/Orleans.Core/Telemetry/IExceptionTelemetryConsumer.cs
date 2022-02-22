using System;
using System.Collections.Generic;

namespace Orleans.Runtime
{
    /// <summary>
    /// Telemetry consumer which tracks an exception.
    /// </summary>
    /// <seealso cref="Orleans.Runtime.ITelemetryConsumer" />
    public interface IExceptionTelemetryConsumer : ITelemetryConsumer
    {
        /// <summary>
        /// Tracks an exception.
        /// </summary>
        /// <param name="exception">The exception.</param>
        /// <param name="properties">The properties.</param>
        /// <param name="metrics">The metrics.</param>
        void TrackException(Exception exception, IDictionary<string, string> properties = null, IDictionary<string, double> metrics = null);
    }
}
