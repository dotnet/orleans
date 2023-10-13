using System.Collections.Generic;
using Orleans.Providers.Streams.Common;

namespace Orleans.Streaming.EventHubs.StatisticMonitors
{
    /// <summary>
    /// Default monitor for Object pool used by EventHubStreamProvider
    /// </summary>
    public class DefaultEventHubBlockPoolMonitor : DefaultBlockPoolMonitor
    {
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="dimensions"></param>
        public DefaultEventHubBlockPoolMonitor(EventHubBlockPoolMonitorDimensions dimensions) : base(new KeyValuePair<string, object>[] { new("Path", dimensions.EventHubPath), new("ObjectPoolId", dimensions.BlockPoolId) })
        {
        }
    }
}
