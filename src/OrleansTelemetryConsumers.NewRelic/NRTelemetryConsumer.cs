using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using NRClient = NewRelic.Api.Agent.NewRelic;

namespace Orleans.TelemetryConsumers.NewRelic
{
    public class NRTelemetryConsumer :  IMetricTelemetryConsumer
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
                metrics.AsParallel().ForAll(m =>
                   {
                       NRClient.AddCustomParameter(m.Key, m.Value);
                   });
            }
        }

        private static void AddProperties(IDictionary<string, string> properties)
        {
            if (properties != null)
            {
                properties.AsParallel().ForAll(p =>
                {
                    NRClient.AddCustomParameter(p.Key, p.Value);
                });
            }
        }
    }
}