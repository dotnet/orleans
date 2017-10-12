using System;
using System.Collections.Generic;

namespace Orleans.Runtime
{
    public interface IMetricTelemetryConsumer : ITelemetryConsumer
    {
        void TrackMetric(string name, double value, IDictionary<string, string> properties = null);

        void TrackMetric(string name, TimeSpan value, IDictionary<string, string> properties = null);

        /// <summary>
        /// Increment a metric value.
        /// </summary>
        /// <param name="name">Name of the metric.</param>
        void IncrementMetric(string name);

        /// <summary>
        /// Increment a metric by a given value.
        /// </summary>
        /// <param name="name">Name of the metric.</param>
        /// <param name="value">Value to increment by.</param>
        void IncrementMetric(string name, double value);

        /// <summary>
        /// Decrement a metric value.
        /// </summary>
        /// <param name="name">Name of the metric.</param>
        void DecrementMetric(string name);

        /// <summary>
        /// Decrement a metric by a given value.
        /// </summary>
        /// <param name="name">Name of the metric.</param>
        /// <param name="value">Value to decrement by.</param>
        void DecrementMetric(string name, double value);
    }
}
