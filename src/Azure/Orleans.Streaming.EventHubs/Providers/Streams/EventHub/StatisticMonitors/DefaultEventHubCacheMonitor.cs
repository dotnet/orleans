using System.Collections.Generic;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.ServiceBus.Providers;

namespace OrleansServiceBus.Providers.Streams.EventHub.StatisticMonitors
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
        /// <param name="telemetryProducer"></param>
        public DefaultEventHubCacheMonitor(EventHubCacheMonitorDimensions dimensions, ITelemetryProducer telemetryProducer)
            :base(telemetryProducer)
        {
            this.LogProperties = new Dictionary<string, string>
            {
                {"Path", dimensions.EventHubPath},
                {"Partition", dimensions.EventHubPartition}
            };
        }
    }
}
