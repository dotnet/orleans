using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.ServiceBus.Providers;

namespace OrleansServiceBus.Providers.Streams.EventHub.StatisticMonitors
{
    /// <summary>
    /// Default monitor for Object pool used by EventHubStreamProvider
    /// </summary>
    public class DefaultEventHubBlockPoolMonitor : IBlockPoolMonitor
    {
        private Logger logger;
        private Dictionary<string, string> logProperties;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="dimensions"></param>
        /// <param name="logger"></param>
        public DefaultEventHubBlockPoolMonitor(EventHubBlockPoolMonitorDimensions dimensions, Logger logger)
        {
            this.logger = logger;
            this.logProperties = new Dictionary<string, string>
            {
                {"Path", dimensions.EventHubPath},
                {"ObjectPoolId", dimensions.BlockPoolId}
            };
        }

        /// <inheritdoc cref="IObjectPoolMonitor"/>
        public void Report(long totalMemoryInByte, long availableMemoryInByte, long claimedMemoryInByte)
        {
            this.logger.TrackMetric("TotalMemoryInByte", totalMemoryInByte, this.logProperties);
            this.logger.TrackMetric("AvailableMemoryInByte", availableMemoryInByte, this.logProperties);
            this.logger.TrackMetric("ClaimedMemoryInByte", claimedMemoryInByte, this.logProperties);
        }

        /// <inheritdoc cref="IObjectPoolMonitor"/>
        public void TrackMemoryReleased(long releasedMemoryInByte)
        {
            this.logger.TrackMetric("ReleasedMemoryInByte", releasedMemoryInByte, this.logProperties);
        }

        /// <inheritdoc cref="IObjectPoolMonitor"/>
        public void TrackMemoryAllocated(long allocatedMemoryInByte)
        {
            this.logger.TrackMetric("AllocatedMemoryInByte", allocatedMemoryInByte, this.logProperties);
        }
    }
}
