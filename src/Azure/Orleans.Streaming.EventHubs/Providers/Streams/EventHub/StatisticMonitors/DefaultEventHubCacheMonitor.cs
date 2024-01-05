using System.Collections.Generic;
using Orleans.Providers.Streams.Common;

namespace Orleans.Streaming.EventHubs.StatisticMonitors
{
    /// <summary>
    /// Default cache monitor for eventhub streaming provider ecosystem
    /// </summary>
    public class DefaultEventHubCacheMonitor : DefaultCacheMonitor
    {
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="dimensions"></param>
        public DefaultEventHubCacheMonitor(EventHubCacheMonitorDimensions dimensions)
            : base(new KeyValuePair<string, object>[] { new("Path", dimensions.EventHubPath), new("Partition", dimensions.EventHubPartition) })
        {
        }
    }
}
