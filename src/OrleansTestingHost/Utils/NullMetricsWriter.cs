using System;
using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.TestingHost.Utils
{
    /// <summary>
    /// Test telemetry client that does nothing with the telemetry.
    /// </summary>
    public class NullTelemetryClient : ITelemetryClient
    {
        void ITelemetryClient.DecrementMetric(string name)
        {
        }

        void ITelemetryClient.DecrementMetric(string name, double value)
        {
        }

        void ITelemetryClient.IncrementMetric(string name)
        {
        }

        void ITelemetryClient.IncrementMetric(string name, double value)
        {
        }

        void ITelemetryClient.TrackDependency(string name, string commandName, DateTimeOffset startTime, TimeSpan duration, bool success)
        {
        }

        void ITelemetryClient.TrackEvent(string name, IDictionary<string, string> properties, IDictionary<string, double> metrics)
        {
        }

        void ITelemetryClient.TrackException(Exception exception, IDictionary<string, string> properties, IDictionary<string, double> metrics)
        {
        }

        void ITelemetryClient.TrackMetric(string name, double value, IDictionary<string, string> properties)
        {
        }

        void ITelemetryClient.TrackMetric(string name, TimeSpan value, IDictionary<string, string> properties)
        {
        }

        void ITelemetryClient.TrackRequest(string name, DateTimeOffset startTime, TimeSpan duration, string responseCode, bool success)
        {
        }

        void ITelemetryClient.TrackTrace(string message)
        {
        }

        void ITelemetryClient.TrackTrace(string message, Severity severityLevel)
        {
        }

        void ITelemetryClient.TrackTrace(string message, Severity severityLevel, IDictionary<string, string> properties)
        {
        }

        void ITelemetryClient.TrackTrace(string message, IDictionary<string, string> properties)
        {
        }
    }
}
