using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime
{
    public class TelemetryManager : ITelemetryProducer, IDisposable
    {
        internal const string ObsoleteMessageTelemetry = "This method might be removed in the future in favor of a non Orleans-owned abstraction for APMs.";

        private List<ITelemetryConsumer> consumers = new List<ITelemetryConsumer>();
        private List<IMetricTelemetryConsumer> metricTelemetryConsumers = new List<IMetricTelemetryConsumer>();
        private List<ITraceTelemetryConsumer> traceTelemetryConsumers = new List<ITraceTelemetryConsumer>();

        public IEnumerable<ITelemetryConsumer> TelemetryConsumers => this.consumers;

        public TelemetryManager(IServiceProvider serviceProvider, IOptions<TelemetryOptions> options)
        {
            var newConsumers = new List<ITelemetryConsumer>(options.Value.Consumers.Count);
            foreach (var consumerType in options.Value.Consumers)
            {
                var consumer = GetTelemetryConsumer(serviceProvider, consumerType);
                newConsumers.Add(consumer);
            }
            this.AddConsumers(newConsumers);
        }

        public void AddConsumers(IEnumerable<ITelemetryConsumer> newConsumers)
        {
            this.consumers = new List<ITelemetryConsumer>(this.consumers.Union(newConsumers));
            this.metricTelemetryConsumers = this.consumers.OfType<IMetricTelemetryConsumer>().ToList();
            this.traceTelemetryConsumers = this.consumers.OfType<ITraceTelemetryConsumer>().ToList();
        }

        private static ITelemetryConsumer GetTelemetryConsumer(IServiceProvider serviceProvider, Type consumerType)
        {
            // first check whether it is registered in the container already
            var consumer = (ITelemetryConsumer)serviceProvider.GetService(consumerType);
            if (consumer == null)
            {
                consumer = (ITelemetryConsumer)ActivatorUtilities.CreateInstance(serviceProvider, consumerType);
            }

            return consumer;
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

        public void Flush()
        {
            List<Exception> exceptions = null;
            var all = this.consumers;
            foreach (var tc in all)
            {
                try
                {
                    tc.Flush();
                }
                catch (Exception ex)
                {
                    (exceptions ?? (exceptions = new List<Exception>())).Add(ex);
                }
            }

            if (exceptions?.Count > 0)
            {
                throw new AggregateException(exceptions);
            }
        }

        public void Close()
        {
            List<Exception> exceptions = null;
            var all = this.consumers;
            this.consumers = new List<ITelemetryConsumer>();
            this.metricTelemetryConsumers = new List<IMetricTelemetryConsumer>();
            this.traceTelemetryConsumers = new List<ITraceTelemetryConsumer>();
            foreach (var tc in all)
            {
                try
                {
                    tc.Close();
                }
                catch (Exception ex)
                {
                    (exceptions ?? (exceptions = new List<Exception>())).Add(ex);
                }

                try
                {
                    (tc as IDisposable)?.Dispose();
                }
                catch (Exception ex)
                {
                    (exceptions ?? (exceptions = new List<Exception>())).Add(ex);
                }
            }

            if (exceptions?.Count > 0)
            {
                throw new AggregateException(exceptions);
            }
        }

        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    this.Close();
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
    }
}
