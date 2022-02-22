using System;
using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.TestingHost.Utils
{
    /// <summary>
    /// Test telemetry producer that does nothing with the telemetry.
    /// </summary>
    public class NullTelemetryProducer : ITelemetryProducer
    {
        /// <summary>
        /// Gets the singleton instance of this class.
        /// </summary>
        /// <value>The instance.</value>
        public static NullTelemetryProducer Instance { get; private set; } = new NullTelemetryProducer();

        private NullTelemetryProducer()
        { }

        /// <inheritdoc />
        void ITelemetryProducer.DecrementMetric(string name)
        {
        }

        /// <inheritdoc />
        void ITelemetryProducer.DecrementMetric(string name, double value)
        {
        }

        /// <inheritdoc />
        void ITelemetryProducer.IncrementMetric(string name)
        {
        }

        /// <inheritdoc />
        void ITelemetryProducer.IncrementMetric(string name, double value)
        {
        }

        /// <inheritdoc />
        void ITelemetryProducer.TrackDependency(string name, string commandName, DateTimeOffset startTime, TimeSpan duration, bool success)
        {
        }

        /// <inheritdoc />
        void ITelemetryProducer.TrackEvent(string name, IDictionary<string, string> properties, IDictionary<string, double> metrics)
        {
        }

        /// <inheritdoc />
        void ITelemetryProducer.TrackException(Exception exception, IDictionary<string, string> properties, IDictionary<string, double> metrics)
        {
        }

        /// <inheritdoc />
        void ITelemetryProducer.TrackMetric(string name, double value, IDictionary<string, string> properties)
        {
        }

        /// <inheritdoc />
        void ITelemetryProducer.TrackMetric(string name, TimeSpan value, IDictionary<string, string> properties)
        {
        }

        /// <inheritdoc />
        void ITelemetryProducer.TrackRequest(string name, DateTimeOffset startTime, TimeSpan duration, string responseCode, bool success)
        {
        }

        /// <inheritdoc />
        void ITelemetryProducer.TrackTrace(string message)
        {
        }

        /// <inheritdoc />
        void ITelemetryProducer.TrackTrace(string message, Severity severityLevel)
        {
        }

        /// <inheritdoc />
        void ITelemetryProducer.TrackTrace(string message, Severity severityLevel, IDictionary<string, string> properties)
        {
        }

        /// <inheritdoc />
        void ITelemetryProducer.TrackTrace(string message, IDictionary<string, string> properties)
        {
        }

        /// <inheritdoc />
        void ITelemetryProducer.Flush()
        {
        }

        /// <inheritdoc />
        void ITelemetryProducer.Close()
        {
        }
    }
}
