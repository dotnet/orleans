using System;
using System.Collections.Generic;

namespace Orleans.Runtime
{
    /// <summary>
    /// Telemetry consumer interface for consuming metrics emitted from the application and runtime.
    /// Implements the <see cref="Orleans.Runtime.ITelemetryConsumer" />
    /// </summary>
    /// <seealso cref="Orleans.Runtime.ITelemetryConsumer" />
    public interface IMetricTelemetryConsumer : ITelemetryConsumer
    {
        /// <summary>
        /// Tracks a named metric.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="value">The value.</param>
        /// <param name="properties">The properties.</param>
        void TrackMetric(string name, double value, IDictionary<string, string> properties = null);

        /// <summary>
        /// Tracks a named metric.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="value">The value.</param>
        /// <param name="properties">The properties.</param>
        void TrackMetric(string name, TimeSpan value, IDictionary<string, string> properties = null);

        /// <summary>
        /// Increments a metric value.
        /// </summary>
        /// <param name="name">Name of the metric.</param>
        void IncrementMetric(string name);

        /// <summary>
        /// Increments a metric by a given value.
        /// </summary>
        /// <param name="name">Name of the metric.</param>
        /// <param name="value">Value to increment by.</param>
        void IncrementMetric(string name, double value);

        /// <summary>
        /// Decrements a metric value.
        /// </summary>
        /// <param name="name">Name of the metric.</param>
        void DecrementMetric(string name);

        /// <summary>
        /// Decrements a metric by a given value.
        /// </summary>
        /// <param name="name">Name of the metric.</param>
        /// <param name="value">Value to decrement by.</param>
        void DecrementMetric(string name, double value);
    }
}
