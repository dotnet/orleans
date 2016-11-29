using System;
using System.Collections.Generic;
using Microsoft.ApplicationInsights;
using Orleans.Runtime;

namespace Orleans.TelemetryConsumers.AI
{
    public class AITelemetryConsumer : ITraceTelemetryConsumer, IEventTelemetryConsumer, IExceptionTelemetryConsumer, 
        IDependencyTelemetryConsumer, IMetricTelemetryConsumer, IRequestTelemetryConsumer
    {
        private TelemetryClient _client;

        public AITelemetryConsumer()
        {
            _client = new TelemetryClient();
        }

        public AITelemetryConsumer(string instrumentationKey)
        {
            _client = new TelemetryClient(new Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration { InstrumentationKey = instrumentationKey });
        }

        public void DecrementMetric(string name)
        {
            _client.TrackMetric(name, -1, null);
        }

        public void DecrementMetric(string name, double value)
        {
            _client.TrackMetric(name, value * -1, null);
        }

        public void IncrementMetric(string name)
        {
            _client.TrackMetric(name, 1, null);
        }

        public void IncrementMetric(string name, double value)
        {
            _client.TrackMetric(name, value, null);
        }

        public void TrackDependency(string dependencyName, string commandName, DateTimeOffset startTime, TimeSpan duration, bool success)
        {
            _client.TrackDependency(dependencyName, commandName, startTime, duration, success);
        }

        public void TrackEvent(string eventName, IDictionary<string, string> properties = null, IDictionary<string, double> metrics = null)
        {
            _client.TrackEvent(eventName, properties, metrics);
        }

        public void TrackException(Exception exception, IDictionary<string, string> properties = null, IDictionary<string, double> metrics = null)
        {
            _client.TrackException(exception, properties, metrics);
        }

        public void TrackMetric(string name, TimeSpan value, IDictionary<string, string> properties = null)
        {
            _client.TrackMetric(name, value.TotalMilliseconds, properties);
        }

        public void TrackMetric(string name, double value, IDictionary<string, string> properties = null)
        {
            _client.TrackMetric(name, value, properties);
        }

        public void TrackRequest(string name, DateTimeOffset startTime, TimeSpan duration, string responseCode, bool success)
        {
            _client.TrackRequest(name, startTime, duration, responseCode, success);
        }

        public void TrackTrace(string message)
        {
            TrackTrace(message, null);
        }

        public void TrackTrace(string message, IDictionary<string, string> properties)
        {
            if (properties != null)
            {
                _client.TrackTrace(message, Microsoft.ApplicationInsights.DataContracts.SeverityLevel.Information, properties);
            }
            else
            {
                _client.TrackTrace(message);
            }
        }

        public void TrackTrace(string message, Severity severity)
        {
            TrackTrace(message, severity, null);
        }

        public void TrackTrace(string message, Severity severity, IDictionary<string, string> properties)
        {
            Microsoft.ApplicationInsights.DataContracts.SeverityLevel sev;

            switch (severity)
            {
                case Severity.Off:
                    return;
                case Severity.Error:
                    sev = Microsoft.ApplicationInsights.DataContracts.SeverityLevel.Error;
                    break;
                case Severity.Warning:
                    sev = Microsoft.ApplicationInsights.DataContracts.SeverityLevel.Warning;
                    break;
                case Severity.Verbose:
                case Severity.Verbose2:
                case Severity.Verbose3:
                    sev = Microsoft.ApplicationInsights.DataContracts.SeverityLevel.Verbose;
                    break;
                default:
                    sev = Microsoft.ApplicationInsights.DataContracts.SeverityLevel.Information;
                    break;
            }

            if (properties == null)
            {
                _client.TrackTrace(message, sev);
            }
            else
            {
                _client.TrackTrace(message, sev, properties);
            }
        }

        public void Flush() { }
        public void Close() { }
    }
}
