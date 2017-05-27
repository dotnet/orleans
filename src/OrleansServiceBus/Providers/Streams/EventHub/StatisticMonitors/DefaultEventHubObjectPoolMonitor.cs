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
    public class DefaultEventHubObjectPoolMonitor : IObjectPoolMonitor
    {
        private Logger logger;
        private Dictionary<string, string> logProperties;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="dimentions"></param>
        /// <param name="logger"></param>
        public DefaultEventHubObjectPoolMonitor(EventHubObjectPoolMonitorDimentions dimentions, Logger logger)
        {
            this.logger = logger;
            this.logProperties = new Dictionary<string, string>
            {
                {"Path", dimentions.EventHubPath},
                {"ObjectPoolId", dimentions.ObjectPoolId}
            };
        }

        /// <inheritdoc cref="IObjectPoolMonitor"/>
        public void Report(long totalBlocks, long freeBlocks, long claimedBlocks)
        {
            this.logger.TrackMetric("TotalBlocks", totalBlocks, this.logProperties);
            this.logger.TrackMetric("FreeBlocks", freeBlocks, this.logProperties);
            this.logger.TrackMetric("ClaimedBlocks", claimedBlocks, this.logProperties);
        }

        /// <inheritdoc cref="IObjectPoolMonitor"/>
        public void TrackObjectReleasedFromCache(int blockCount)
        {
            this.logger.TrackMetric("BlockReleasedCount", blockCount, this.logProperties);
        }

        /// <inheritdoc cref="IObjectPoolMonitor"/>
        public void TrackObjectAllocatedByCache(int blockCount)
        {
            this.logger.TrackMetric("BlockAllocatedCount", blockCount, this.logProperties);
        }
    }
}
