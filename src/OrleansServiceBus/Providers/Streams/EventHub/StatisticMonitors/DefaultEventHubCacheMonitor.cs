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
    /// Default cache monitor for eventhub streaming provider ecosystem
    /// </summary>
    public class DefaultEventHubCacheMonitor : ICacheMonitor
    {
        private Logger logger;
        private Dictionary<string, string> logProperties;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="dimentions"></param>
        /// <param name="logger"></param>
        public DefaultEventHubCacheMonitor(EventHubCacheMonitorDimentions dimentions, Logger logger)
        {
            this.logger = logger;
            this.logProperties = new Dictionary<string, string>
            {
                {"Path", dimentions.EventHubPath},
                {"Partition", dimentions.EventHubPartition}
            };
        }

        /// <inheritdoc cref="ICacheMonitor"/>
        public void ReportCacheSize(long totalCacheSizeInByte)
        {
            logger.TrackMetric("TotalCacheSizeInByte", totalCacheSizeInByte, this.logProperties);
        }

        /// <inheritdoc cref="ICacheMonitor"/>
        public void ReportMessageStatistics(TimeSpan? oldestMessageAge, TimeSpan? oldestMessageEnqueueTimeToNow, TimeSpan? newestMessageEnqueueTimeToNow, long totalMessageCount)
        {
            if(oldestMessageAge.HasValue)
                logger.TrackMetric("OldestMessageAge", oldestMessageAge.Value, this.logProperties);

            if(oldestMessageEnqueueTimeToNow.HasValue)
                logger.TrackMetric("OldestMessageEnqueueTimeToNow", oldestMessageEnqueueTimeToNow.Value, this.logProperties);

            if(newestMessageEnqueueTimeToNow.HasValue)
                logger.TrackMetric("NewestMessageEnqueueTimeToNow", newestMessageEnqueueTimeToNow.Value, this.logProperties);

            logger.TrackMetric("TotalMessageCount", totalMessageCount, this.logProperties);
        }

        /// <inheritdoc cref="ICacheMonitor"/>
        public void TrackMemoryAllocated(int memoryInByte)
        {
            logger.TrackMetric("MemoryAllocatedInByte", memoryInByte, this.logProperties);
        }

        /// <inheritdoc cref="ICacheMonitor"/>
        public void TrackMemoryReleased(int memoryInByte)
        {
            logger.TrackMetric("MemoryReleasedInByte", memoryInByte, this.logProperties);
        }

        /// <inheritdoc cref="ICacheMonitor"/>
        public void TrackMessageAdded(long mesageAdded)
        {
            logger.TrackMetric("MessageAdded", mesageAdded, this.logProperties);
        }

        /// <inheritdoc cref="ICacheMonitor"/>
        public void TrackMessagePurged(long messagePurged)
        {
            logger.TrackMetric("MessagePurged", messagePurged, this.logProperties);
        }
    }
}
