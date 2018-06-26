using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Benchmarks.Transactions
{
    public class LogMetricsTelemetryConsumer : IMetricTelemetryConsumer
    {
        private ILogger logger;
        public LogMetricsTelemetryConsumer(ILogger<LogMetricsTelemetryConsumer> logger)
        {
            this.logger = logger;
        }

        public void Close()
        {
            //do nothing
        }

        public void DecrementMetric(string name)
        {
            //do nothing
        }

        public void DecrementMetric(string name, double value)
        {
            //do nothing
        }

        public void Flush()
        {
            //do nothing
        }

        public void IncrementMetric(string name)
        {
            //do nothing
        }

        public void IncrementMetric(string name, double value)
        {
            //do nothing
        }

        public void TrackMetric(string name, double value, IDictionary<string, string> properties = null)
        {
            if (!name.Contains("Transaction"))
                return;
            if (properties != null)
            {
                foreach (var property in properties)
                {
                    this.logger.LogInformation($"{property.Key}:{property.Value}");
                }
            }

            this.logger.LogInformation($"{name}={value}");

        }

        public void TrackMetric(string name, TimeSpan value, IDictionary<string, string> properties = null)
        {
            if (!name.Contains("Transaction"))
                return;
            if (properties != null)
            {
                foreach (var property in properties)
                {
                    this.logger.LogInformation($"{property.Key}:{property.Value}");
                }
            }

            this.logger.LogInformation($"{name}={value}");
        }
    }
}
