using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Runtime;
using StatsdClient;

namespace Orleans.TelemetryConsumers.Datadog
{
    public class DatadogTelemetryConsumer : IMetricTelemetryConsumer, IDisposable
    {
        private readonly DogStatsdService service;

        public DatadogTelemetryConsumer()
        {
            var statsdConfig = new StatsdConfig();

            this.service = new DogStatsdService();
            this.service.Configure(statsdConfig);
        }

        public void DecrementMetric(string name) => this.service.Decrement(name);

        public void DecrementMetric(string name, double value) => this.service.Decrement(name, Clamp(value));

        public void IncrementMetric(string name) => this.service.Increment(name);

        public void IncrementMetric(string name, double value) => this.service.Increment(name, Clamp(value));

        public void TrackMetric(string name, TimeSpan value, IDictionary<string, string> properties = null) =>
            this.service.Distribution(name, value.TotalMilliseconds, tags: ToTags(properties));

        public void TrackMetric(string name, double value, IDictionary<string, string> properties = null) =>
            this.service.Distribution(name, value, tags: ToTags(properties));

        public void Flush() => this.service.Flush();

        public void Close() => this.service.Flush();

        public void Dispose() => this.service.Dispose();

        private int Clamp(double value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);

        private string[] ToTags(IDictionary<string, string> properties) => properties?.Select(ToTag).ToArray();

        private static string ToTag(KeyValuePair<string, string> property) =>
            $"{property.Key.Replace(':', '_')}:{property.Value.Replace(':', '_')}";
    }
}