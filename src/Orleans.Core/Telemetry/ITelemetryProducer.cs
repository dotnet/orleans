using System;
using System.Collections.Generic;

namespace Orleans.Runtime
{
    /// <summary>Represents an object used for producing telemetry</summary>
    public interface ITelemetryProducer
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

        [Obsolete(TelemetryManager.ObsoleteMessageTelemetry)]
        void TrackDependency(string name, string commandName, DateTimeOffset startTime, TimeSpan duration, bool success);

        [Obsolete(TelemetryManager.ObsoleteMessageTelemetry)]
        void TrackEvent(string name, IDictionary<string, string> properties = null, IDictionary<string, double> metrics = null);

        [Obsolete(TelemetryManager.ObsoleteMessageTelemetry)]
        void TrackRequest(string name, DateTimeOffset startTime, TimeSpan duration, string responseCode, bool success);

        [Obsolete(TelemetryManager.ObsoleteMessageTelemetry)]
        void TrackException(Exception exception, IDictionary<string, string> properties = null, IDictionary<string, double> metrics = null);

        [Obsolete(TelemetryManager.ObsoleteMessageTelemetry)]
        void TrackTrace(string message);

        [Obsolete(TelemetryManager.ObsoleteMessageTelemetry)]
        void TrackTrace(string message, Severity severityLevel);

        [Obsolete(TelemetryManager.ObsoleteMessageTelemetry)]
        void TrackTrace(string message, Severity severityLevel, IDictionary<string, string> properties);

        [Obsolete(TelemetryManager.ObsoleteMessageTelemetry)]
        void TrackTrace(string message, IDictionary<string, string> properties);

        /// <summary>
        /// Flushes this instance.
        /// </summary>
        void Flush();

        /// <summary>
        /// Closes this instance.
        /// </summary>
        void Close();
    }
}
