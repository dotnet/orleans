using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime
{
    public class TelemetryManager : ITelemetryClient, IDisposable
    {
        internal const string ObsoleteMessageTelemetry = "This method might be removed in the future in favor of a non Orleans-owned abstraction for APMs.";

        private List<ITelemetryConsumer> consumers;
        private List<IMetricTelemetryConsumer> metricTelemetryConsumers;
        private List<ITraceTelemetryConsumer> traceTelemetryConsumers;

        public TelemetryManager(IEnumerable<ITelemetryConsumer> consumers)
        {
            this.consumers = consumers.ToList();
            this.metricTelemetryConsumers = consumers.OfType<IMetricTelemetryConsumer>().ToList();
            this.traceTelemetryConsumers = consumers.OfType<ITraceTelemetryConsumer>().ToList();
        }

        internal static TelemetryManager FromConfiguration(TelemetryConfiguration configuration, IServiceProvider serviceProvider)
        {
            var consumers = new List<ITelemetryConsumer>(configuration.Consumers.Count);
            foreach (var consumerConfig in configuration.Consumers)
            {
                ITelemetryConsumer consumer = null;
                if ((consumerConfig.Properties?.Count ?? 0) == 0)
                {
                    // first check whether it is registered in the container already
                    consumer = (ITelemetryConsumer)serviceProvider.GetService(consumerConfig.ConsumerType);
                }
                if (consumer == null)
                {
                    consumer = (ITelemetryConsumer)ActivatorUtilities.CreateInstance(serviceProvider, consumerConfig.ConsumerType, consumerConfig.Properties?.Values?.ToArray() ?? new object[0]);
                }
                consumers.Add(consumer);
            }

            return new TelemetryManager(consumers);
        }

        public void TrackMetric(string name, double value, IDictionary<string, string> properties = null)
        {
            foreach (var tc in this.metricTelemetryConsumers)
            {
                tc.TrackMetric(name, value, properties);
            }
        }

        public void TrackMetric(string name, TimeSpan value, IDictionary<string, string> properties = null)
        {
            foreach (var tc in this.metricTelemetryConsumers)
            {
                tc.TrackMetric(name, value, properties);
            }
        }

        public void IncrementMetric(string name)
        {
            foreach (var tc in this.metricTelemetryConsumers)
            {
                tc.IncrementMetric(name);
            }
        }

        public void IncrementMetric(string name, double value)
        {
            foreach (var tc in this.metricTelemetryConsumers)
            {
                tc.IncrementMetric(name, value);
            }
        }

        public void DecrementMetric(string name)
        {
            foreach (var tc in this.metricTelemetryConsumers)
            {
                tc.DecrementMetric(name);
            }
        }

        public void DecrementMetric(string name, double value)
        {
            foreach (var tc in this.metricTelemetryConsumers)
            {
                tc.DecrementMetric(name, value);
            }
        }

        public void TrackDependency(string name, string commandName, DateTimeOffset startTime, TimeSpan duration, bool success)
        {
            foreach (var tc in this.consumers.OfType<IDependencyTelemetryConsumer>())
            {
                tc.TrackDependency(name, commandName, startTime, duration, success);
            }
        }

        public void TrackEvent(string name, IDictionary<string, string> properties = null, IDictionary<string, double> metrics = null)
        {
            foreach (var tc in this.consumers.OfType<IEventTelemetryConsumer>())
            {
                tc.TrackEvent(name, properties, metrics);
            }
        }

        public void TrackRequest(string name, DateTimeOffset startTime, TimeSpan duration, string responseCode, bool success)
        {
            foreach (var tc in this.consumers.OfType<IRequestTelemetryConsumer>())
            {
                tc.TrackRequest(name, startTime, duration, responseCode, success);
            }
        }

        public void TrackException(Exception exception, IDictionary<string, string> properties = null, IDictionary<string, double> metrics = null)
        {
            foreach (var tc in this.consumers.OfType<IExceptionTelemetryConsumer>())
            {
                tc.TrackException(exception, properties, metrics);
            }
        }

        public void TrackTrace(string message)
        {
            foreach (var tc in this.traceTelemetryConsumers)
            {
                tc.TrackTrace(message);
            }
        }

        public void TrackTrace(string message, Severity severity)
        {
            foreach (var tc in this.traceTelemetryConsumers)
            {
                tc.TrackTrace(message, severity);
            }
        }

        public void TrackTrace(string message, Severity severity, IDictionary<string, string> properties)
        {
            foreach (var tc in this.traceTelemetryConsumers)
            {
                tc.TrackTrace(message, severity, properties);
            }
        }

        public void TrackTrace(string message, IDictionary<string, string> properties)
        {
            foreach (var tc in this.traceTelemetryConsumers)
            {
                tc.TrackTrace(message, properties);
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
                    this.consumers = new List<ITelemetryConsumer>();
                    this.metricTelemetryConsumers = new List<IMetricTelemetryConsumer>();
                    this.traceTelemetryConsumers = new List<ITraceTelemetryConsumer>();
                    foreach (var tc in all)
                    {
                        try
                        {
                            tc.Flush();
                        }
                        catch (Exception) { }
                        try
                        {
                            tc.Close();
                        }
                        catch (Exception) { }
                        try
                        {
                            (tc as IDisposable).Dispose();
                        }
                        catch (Exception) { }
                    }
                }

                disposedValue = true;
            }
        }

        // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
        // ~TelemetryManager() {
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
