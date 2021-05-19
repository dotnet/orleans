using System.Collections.Generic;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.ServiceBus.Providers;

namespace OrleansServiceBus.Providers.Streams.EventHub.StatisticMonitors
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
        /// <param name="telemetryProducer"></param>
        public DefaultEventHubBlockPoolMonitor(EventHubBlockPoolMonitorDimensions dimensions, ITelemetryProducer telemetryProducer)
            :base(telemetryProducer)
        {
            this.LogProperties = new Dictionary<string, string>
            {
                {"Path", dimensions.EventHubPath},
                {"ObjectPoolId", dimensions.BlockPoolId}
            };
        }
    }
}
