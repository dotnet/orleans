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
        /// <param name="dimensions"></param>
        /// <param name="logger"></param>
        public DefaultEventHubCacheMonitor(EventHubCacheMonitorDimensions dimensions, Logger logger)
        {
            this.logger = logger;
            this.logProperties = new Dictionary<string, string>
            {
                {"Path", dimensions.EventHubPath},
                {"Partition", dimensions.EventHubPartition}
            };
        }

        /// <inheritdoc cref="ICacheMonitor"/>
        public void TrackCachePressureMonitorStatusChange(string pressureMonitorType, bool underPressure, double? cachePressureContributionCount, double? currentPressure,
            double? flowControlThreshold)
        {
            logger.TrackMetric($"{pressureMonitorType}-UnderPressure", underPressure ? 1 : 0, this.logProperties);
            if(cachePressureContributionCount.HasValue)
                logger.TrackMetric($"{pressureMonitorType}-PressureContributionCount", cachePressureContributionCount.Value, this.logProperties);
            if(currentPressure.HasValue)
                logger.TrackMetric($"{pressureMonitorType}-CurrentPressure", currentPressure.Value, this.logProperties);
        }

        /// <inheritdoc cref="ICacheMonitor"/>
        public void ReportCacheSize(long totalCacheSizeInByte)
        {
            logger.TrackMetric("TotalCacheSizeInByte", totalCacheSizeInByte, this.logProperties);
        }

        /// <inheritdoc cref="ICacheMonitor"/>
        public void ReportMessageStatistics(DateTime? oldestMessageEnqueueTimeUtc, DateTime? oldestMessageDequeueTimeUtc, DateTime? newestMessageEnqueueTimeUtc, long totalMessageCount)
        {
            if(oldestMessageEnqueueTimeUtc.HasValue && newestMessageEnqueueTimeUtc.HasValue)
                logger.TrackMetric("OldestMessageRelativeAgeToNewestMessage", newestMessageEnqueueTimeUtc.Value - oldestMessageEnqueueTimeUtc.Value, this.logProperties);

            if(oldestMessageDequeueTimeUtc.HasValue)
                logger.TrackMetric("OldestMessageDequeueTimeToNow", DateTime.UtcNow - oldestMessageDequeueTimeUtc.Value, this.logProperties);

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
        public void TrackMessagesAdded(long mesageAdded)
        {
            logger.TrackMetric("MessageAdded", mesageAdded, this.logProperties);
        }

        /// <inheritdoc cref="ICacheMonitor"/>
        public void TrackMessagesPurged(long messagePurged)
        {
            logger.TrackMetric("MessagePurged", messagePurged, this.logProperties);
        }
    }
}
