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
        public void Report(long objectPoolSizeInByte, long freeMemoryInByte, long claimedMemoryInByte)
        {
            this.logger.TrackMetric("PoolSizeInByte", objectPoolSizeInByte, this.logProperties);
            this.logger.TrackMetric("PoolFreeMemroyInByte", freeMemoryInByte, this.logProperties);
            this.logger.TrackMetric("PoolClaimedMemoryInByte", claimedMemoryInByte, this.logProperties);
        }

        /// <inheritdoc cref="IObjectPoolMonitor"/>
        public void TrackMemoryReleasedFromCache(int memoryInByte)
        {
            this.logger.TrackMetric("MemoryReleasedInByte", memoryInByte, this.logProperties);
        }

        /// <inheritdoc cref="IObjectPoolMonitor"/>
        public void TrackMemroyAllocatedByCache(int memoryInByte)
        {
            this.logger.TrackMetric("MemoryAllocatedInByte", memoryInByte, this.logProperties);
        }
    }
}
