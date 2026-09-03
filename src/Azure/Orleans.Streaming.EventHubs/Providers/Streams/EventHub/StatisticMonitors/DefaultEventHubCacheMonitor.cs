using System.Collections.Generic;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;

namespace Orleans.Streaming.EventHubs.StatisticMonitors
{
    /// <summary>
    /// Default cache monitor for eventhub streaming provider ecosystem
    /// </summary>
    public class DefaultEventHubCacheMonitor : DefaultCacheMonitor
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultEventHubCacheMonitor"/> class.
        /// </summary>
        /// <param name="dimensions">The Event Hub cache metric dimensions.</param>
        /// <param name="instruments">The Orleans runtime instruments.</param>
        public DefaultEventHubCacheMonitor(EventHubCacheMonitorDimensions dimensions, OrleansInstruments instruments)
            : base(new KeyValuePair<string, object>[] { new("Path", dimensions.EventHubPath), new("Partition", dimensions.EventHubPartition) }, instruments)
        {
        }
    }
}
