using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime
{
    public class MetricsWriter : IMetricsWriter, IDisposable
    {
        private List<IMetricTelemetryConsumer> consumers;

        public MetricsWriter(IEnumerable<IMetricTelemetryConsumer> consumers)
        {
            this.consumers = consumers.ToList();
        }

        internal static MetricsWriter FromConfiguration(MetricsTelemetryConfiguration configuration, IServiceProvider serviceProvider)
        {
            var consumers = new List<IMetricTelemetryConsumer>(configuration.Consumers.Count);
            foreach (var consumerConfig in configuration.Consumers)
            {
                IMetricTelemetryConsumer consumer = null;
                if ((consumerConfig.Properties?.Count ?? 0) == 0)
                {
                    // first check whether it is registered in the container already
                    consumer = (IMetricTelemetryConsumer)serviceProvider.GetService(consumerConfig.ConsumerType);
                }
                if (consumer == null)
                {
                    consumer = (IMetricTelemetryConsumer)ActivatorUtilities.CreateInstance(serviceProvider, consumerConfig.ConsumerType, consumerConfig.Properties?.Values?.ToArray() ?? new object[0]);
                }
                consumers.Add(consumer);
            }

            return new MetricsWriter(consumers);
        }

        public void TrackMetric(string name, double value, IDictionary<string, string> properties = null)
        {
            foreach (var tc in this.consumers)
            {
                tc.TrackMetric(name, value, properties);
            }
        }

        public void TrackMetric(string name, TimeSpan value, IDictionary<string, string> properties = null)
        {
            foreach (var tc in this.consumers)
            {
                tc.TrackMetric(name, value, properties);
            }
        }

        public void IncrementMetric(string name)
        {
            foreach (var tc in this.consumers)
            {
                tc.IncrementMetric(name);
            }
        }

        public void IncrementMetric(string name, double value)
        {
            foreach (var tc in this.consumers)
            {
                tc.IncrementMetric(name, value);
            }
        }

        public void DecrementMetric(string name)
        {
            foreach (var tc in this.consumers)
            {
                tc.DecrementMetric(name);
            }
        }

        public void DecrementMetric(string name, double value)
        {
            foreach (var tc in this.consumers)
            {
                tc.DecrementMetric(name, value);
            }
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    var all = this.consumers;
                    this.consumers = new List<IMetricTelemetryConsumer>();
                    foreach (var tc in all.OfType<IDisposable>())
                    {
                        try
                        {
                            tc.Dispose();
                        }
                        catch (Exception) { }
                    }
                }

                disposedValue = true;
            }
        }

        // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
        // ~MetricsWriter() {
        //   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
        //   Dispose(false);
        // }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            // TODO: uncomment the following line if the finalizer is overridden above.
            // GC.SuppressFinalize(this);
        }
        #endregion
    }
}
