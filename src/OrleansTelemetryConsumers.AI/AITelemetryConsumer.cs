using System;
using System.Collections.Generic;
using Microsoft.ApplicationInsights;
using Orleans.Runtime;

namespace Orleans.TelemetryConsumers.AI
{
    public class AITelemetryConsumer : IMetricTelemetryConsumer, IDisposable
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

        public void TrackMetric(string name, TimeSpan value, IDictionary<string, string> properties = null)
        {
            _client.TrackMetric(name, value.TotalMilliseconds, properties);
        }

        public void TrackMetric(string name, double value, IDictionary<string, string> properties = null)
        {
            _client.TrackMetric(name, value, properties);
        }

        public void Dispose()
        {
            _client.Flush();
        }
    }
}
