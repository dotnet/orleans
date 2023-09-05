using System.Collections.Generic;
using Orleans.Providers.Streams.Common;

namespace Orleans.Streaming.EventHubs
{
    /// <summary>
    /// Default EventHub receiver monitor that tracks metrics using loggers PKI support.
    /// </summary>
    public class DefaultEventHubReceiverMonitor : DefaultQueueAdapterReceiverMonitor
    {
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="dimensions">Aggregation Dimension bag for EventhubReceiverMonitor</param>
        public DefaultEventHubReceiverMonitor(EventHubReceiverMonitorDimensions dimensions)
            : base(new KeyValuePair<string, object>[] { new("Path", dimensions.EventHubPath), new("Partition", dimensions.EventHubPartition) })
        {
        }
    }

}
