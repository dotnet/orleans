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
            NRClient.RecordMetric(name, -1);
        }

        public void DecrementMetric(string name, double value)
        {
            NRClient.RecordMetric(name, (float)value * -1);
        }

        public void IncrementMetric(string name)
        {
            NRClient.RecordMetric(name, 1);
            NRClient.IncrementCounter(name);            
        }

        public void IncrementMetric(string name, double value)
        {
            NRClient.RecordMetric(name, (float)value);
        }

        public void TrackDependency(string dependencyName, string commandName, DateTimeOffset startTime, TimeSpan duration, bool success)
        {
            NRClient.RecordResponseTimeMetric(string.Format("{0}\\{1}", dependencyName, commandName), (long)duration.TotalMilliseconds);
        }

        public void TrackEvent(string eventName, IDictionary<string, string> properties = null, IDictionary<string, double> metrics = null)
        {
            NRClient.RecordCustomEvent(eventName, metrics != null ? metrics.ToDictionary(e=>e.Key, e=> (object)e.Value) : null);
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
            NRClient.RecordMetric(name, (float)value.TotalMilliseconds);
        }

        public void TrackMetric(string name, double value, IDictionary<string, string> properties = null)
        {
            AddProperties(properties);
            NRClient.RecordMetric(name, (float)value);
        }

        public void TrackRequest(string name, DateTimeOffset startTime, TimeSpan duration, string responseCode, bool success)
        {
            NRClient.RecordMetric(name, (float)duration.TotalMilliseconds);
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

        public void Flush() { }
        public void Close() { }
    }
}
