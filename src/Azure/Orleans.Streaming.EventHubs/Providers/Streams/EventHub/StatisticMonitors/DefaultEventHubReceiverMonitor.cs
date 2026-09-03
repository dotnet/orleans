using System.Collections.Generic;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;

namespace Orleans.Streaming.EventHubs
{
    /// <summary>
    /// Default EventHub receiver monitor that tracks metrics using loggers PKI support.
    /// </summary>
    public class DefaultEventHubReceiverMonitor : DefaultQueueAdapterReceiverMonitor
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultEventHubReceiverMonitor"/> class.
        /// </summary>
        /// <param name="dimensions">The Event Hub receiver metric dimensions.</param>
        /// <param name="instruments">The Orleans runtime instruments.</param>
        public DefaultEventHubReceiverMonitor(EventHubReceiverMonitorDimensions dimensions, OrleansInstruments instruments)
            : base(new KeyValuePair<string, object>[] { new("Path", dimensions.EventHubPath), new("Partition", dimensions.EventHubPartition) }, instruments)
        {
        }
    }

}
