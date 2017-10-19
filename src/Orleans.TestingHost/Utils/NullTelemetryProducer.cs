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
        void ITelemetryProducer.DecrementMetric(string name)
        {
        }

        void ITelemetryProducer.DecrementMetric(string name, double value)
        {
        }

        void ITelemetryProducer.IncrementMetric(string name)
        {
        }

        void ITelemetryProducer.IncrementMetric(string name, double value)
        {
        }

        void ITelemetryProducer.TrackDependency(string name, string commandName, DateTimeOffset startTime, TimeSpan duration, bool success)
        {
        }

        void ITelemetryProducer.TrackEvent(string name, IDictionary<string, string> properties, IDictionary<string, double> metrics)
        {
        }

        void ITelemetryProducer.TrackException(Exception exception, IDictionary<string, string> properties, IDictionary<string, double> metrics)
        {
        }

        void ITelemetryProducer.TrackMetric(string name, double value, IDictionary<string, string> properties)
        {
        }

        void ITelemetryProducer.TrackMetric(string name, TimeSpan value, IDictionary<string, string> properties)
        {
        }

        void ITelemetryProducer.TrackRequest(string name, DateTimeOffset startTime, TimeSpan duration, string responseCode, bool success)
        {
        }

        void ITelemetryProducer.TrackTrace(string message)
        {
        }

        void ITelemetryProducer.TrackTrace(string message, Severity severityLevel)
        {
        }

        void ITelemetryProducer.TrackTrace(string message, Severity severityLevel, IDictionary<string, string> properties)
        {
        }

        void ITelemetryProducer.TrackTrace(string message, IDictionary<string, string> properties)
        {
        }

        void ITelemetryProducer.Flush()
        {
        }

        void ITelemetryProducer.Close()
        {
        }
    }
}
