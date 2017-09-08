using System;
using System.Collections.Generic;

namespace Orleans.Runtime
{
    /// <summary>Represents an object used for publishing metrics</summary>
    /// <remarks>In a future iteration, this will probably be replaced by an IMetric interface that already contains the name of the metric,
    /// so callers will not need to specify it in every method call</remarks>
    public interface IMetricsWriter
    {
        /// <summary>Send a metric value to the registered telemetry consumers.</summary>
        /// <param name="name">Metric name.</param>
        /// <param name="value">Metric value.</param>
        /// <param name="properties">Named string values you can use to classify and filter metrics.</param>
        void TrackMetric(string name, double value, IDictionary<string, string> properties = null);

        /// <summary>Send a metric value to the registered telemetry consumers.</summary>
        /// <param name="name">Metric name.</param>
        /// <param name="value">Metric value.</param>
        /// <param name="properties">Named string values you can use to classify and filter metrics.</param>
        void TrackMetric(string name, TimeSpan value, IDictionary<string, string> properties = null);

        /// <summary>Send a metric value of 1 to the registered telemetry consumers, to be displayed in an aggregated view.</summary>
        /// <param name="name">Metric name.</param>
        void IncrementMetric(string name);

        /// <summary>Send a metric value of <paramref name="value"/> to the registered telemetry consumers, to be displayed in an aggregated view.</summary>
        /// <param name="name">Metric name.</param>
        /// <param name="value">The amount to increment.</param>
        void IncrementMetric(string name, double value);

        /// <summary>Send a metric value of -1 to the registered telemetry consumers, to be displayed in an aggregated view.</summary>
        /// <param name="name">Metric name.</param>
        void DecrementMetric(string name);

        /// <summary>Send a metric value of -<paramref name="value"/> to the registered telemetry consumers, to be displayed in an aggregated view.</summary>
        /// <param name="name">Metric name.</param>
        /// <param name="value">The amount to decrement. The value should typically be positive.</param>
        void DecrementMetric(string name, double value);
    }
}
