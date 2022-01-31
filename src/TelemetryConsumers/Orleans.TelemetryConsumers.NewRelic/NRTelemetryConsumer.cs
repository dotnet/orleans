using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using NRClient = NewRelic.Api.Agent.NewRelic;

namespace Orleans.TelemetryConsumers.NewRelic
{
    public class NRTelemetryConsumer : IEventTelemetryConsumer, IExceptionTelemetryConsumer,
        IDependencyTelemetryConsumer, IMetricTelemetryConsumer, IRequestTelemetryConsumer
    {
        public NRTelemetryConsumer()
        {
            NRClient.StartAgent();
        }

        public void DecrementMetric(string name)
        {
            NRClient.RecordMetric(FormatMetricName(name), -1);
        }

        public void DecrementMetric(string name, double value)
        {
            NRClient.RecordMetric(FormatMetricName(name), (float)value * -1);
        }

        public void IncrementMetric(string name)
        {
            NRClient.RecordMetric(FormatMetricName(name), 1);
            NRClient.IncrementCounter(name);
        }

        public void IncrementMetric(string name, double value)
        {
            NRClient.RecordMetric(FormatMetricName(name), (float)value);
        }

        public void TrackDependency(string dependencyName, string commandName, DateTimeOffset startTime, TimeSpan duration, bool success)
        {
            NRClient.RecordResponseTimeMetric(FormatMetricName($"{dependencyName}/{commandName}"), (long)duration.TotalMilliseconds);
        }

        public void TrackEvent(string eventName, IDictionary<string, string> properties = null, IDictionary<string, double> metrics = null)
        {
            NRClient.RecordCustomEvent(eventName, metrics != null ? metrics.ToDictionary(e => e.Key, e => (object)e.Value) : null);
            AddMetric(metrics);
            AddProperties(properties);
        }

        public void TrackException(Exception exception, IDictionary<string, string> properties = null, IDictionary<string, double> metrics = null)
        {
            AddMetric(metrics);
            NRClient.NoticeError(exception, properties);
        }

        public void TrackMetric(string name, TimeSpan value, IDictionary<string, string> properties = null)
        {
            AddProperties(properties);
            NRClient.RecordMetric(FormatMetricName(name), (float)value.TotalMilliseconds);
        }

        public void TrackMetric(string name, double value, IDictionary<string, string> properties = null)
        {
            AddProperties(properties);
            NRClient.RecordMetric(FormatMetricName(name), (float)value);
        }

        public void TrackRequest(string name, DateTimeOffset startTime, TimeSpan duration, string responseCode, bool success)
        {
            NRClient.RecordMetric(FormatMetricName(name), (float)duration.TotalMilliseconds);
        }

        private static string FormatMetricName(string name)
        {
            // according to NR docs https://docs.newrelic.com/docs/agents/manage-apm-agents/agent-data/custom-metrics
            // if is required to prefix all custom metrics with "Custom/"
            return "Custom/" + name;
        }

        private static void AddMetric(IDictionary<string, double> metrics)
        {
            if (metrics != null)
            {
                var agent = NRClient.GetAgent();
                var transaction = agent.CurrentTransaction;
                foreach (var metric in metrics)
                {
                   transaction.AddCustomAttribute(metric.Key, metric.Value);
                }
            }
        }

        private static void AddProperties(IDictionary<string, string> properties)
        {
            if (properties != null)
            {
                var agent = NRClient.GetAgent();
                var transaction = agent.CurrentTransaction;
                foreach (var property in properties)
                {
                   transaction.AddCustomAttribute(property.Key, property.Value);
                }
            }
        }

        public void Flush()
        {
        }

        public void Close()
        {
        }
    }
}