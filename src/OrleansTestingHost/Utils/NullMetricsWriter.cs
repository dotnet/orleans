using System;
using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.TestingHost.Utils
{
    /// <summary>
    /// Test metrics writer that does nothing with the metrics.
    /// </summary>
    public class NullMetricsWriter : IMetricsWriter
    {
        /// <inheritdoc />
        public void DecrementMetric(string name)
        {
        }

        /// <inheritdoc />
        public void DecrementMetric(string name, double value)
        {
        }

        /// <inheritdoc />
        public void IncrementMetric(string name)
        {
        }

        /// <inheritdoc />
        public void IncrementMetric(string name, double value)
        {
        }

        /// <inheritdoc />
        public void TrackMetric(string name, double value, IDictionary<string, string> properties = null)
        {
        }

        /// <inheritdoc />
        public void TrackMetric(string name, TimeSpan value, IDictionary<string, string> properties = null)
        {
        }
    }
}
