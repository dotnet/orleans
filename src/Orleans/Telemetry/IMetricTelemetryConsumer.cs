using System;
using System.Collections.Generic;

namespace Orleans.Runtime
{
    public interface IMetricTelemetryConsumer : ITelemetryConsumer
    {
        void TrackMetric(string name, double value, IDictionary<string, string> properties = null);
        void TrackMetric(string name, TimeSpan value, IDictionary<string, string> properties = null);
        void IncrementMetric(string name);
        void IncrementMetric(string name, double value);
        void DecrementMetric(string name);
        void DecrementMetric(string name, double value);
    }
}
