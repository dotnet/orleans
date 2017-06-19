using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// cache monitor used as a default option in GeneratorStreamprovider and MemoryStreamProvider
    /// </summary>
    public class DefaultCacheMonitor : ICacheMonitor
    {
        protected Logger Logger;
        protected Dictionary<string, string> LogProperties;

        public DefaultCacheMonitor(Logger logger)
        {
            this.Logger = logger;
        }

        public DefaultCacheMonitor(CacheMonitorDimensions dimensions, Logger logger)
            :this(logger)
        {
            this.LogProperties = new Dictionary<string, string>
            {
                {"QueueId", dimensions.QueueId},
                {"HostName", dimensions.NodeConfig.HostNameOrIPAddress}
            };
        }
        /// <inheritdoc cref="ICacheMonitor"/>
        public void TrackCachePressureMonitorStatusChange(string pressureMonitorType, bool underPressure, double? cachePressureContributionCount, double? currentPressure,
            double? flowControlThreshold)
        {
            this.Logger.TrackMetric($"{pressureMonitorType}-UnderPressure", underPressure ? 1 : 0, this.LogProperties);
            if (cachePressureContributionCount.HasValue)
                this.Logger.TrackMetric($"{pressureMonitorType}-PressureContributionCount", cachePressureContributionCount.Value, this.LogProperties);
            if (currentPressure.HasValue)
                this.Logger.TrackMetric($"{pressureMonitorType}-CurrentPressure", currentPressure.Value, this.LogProperties);
        }

        /// <inheritdoc cref="ICacheMonitor"/>
        public void ReportCacheSize(long totalCacheSizeInByte)
        {
            this.Logger.TrackMetric("TotalCacheSizeInByte", totalCacheSizeInByte, this.LogProperties);
        }

        /// <inheritdoc cref="ICacheMonitor"/>
        public void ReportMessageStatistics(DateTime? oldestMessageEnqueueTimeUtc, DateTime? oldestMessageDequeueTimeUtc, DateTime? newestMessageEnqueueTimeUtc, long totalMessageCount)
        {
            if (oldestMessageEnqueueTimeUtc.HasValue && newestMessageEnqueueTimeUtc.HasValue)
                this.Logger.TrackMetric("OldestMessageRelativeAgeToNewestMessage", newestMessageEnqueueTimeUtc.Value - oldestMessageEnqueueTimeUtc.Value, this.LogProperties);

            if (oldestMessageDequeueTimeUtc.HasValue)
                this.Logger.TrackMetric("OldestMessageDequeueTimeToNow", DateTime.UtcNow - oldestMessageDequeueTimeUtc.Value, this.LogProperties);

            this.Logger.TrackMetric("TotalMessageCount", totalMessageCount, this.LogProperties);
        }

        /// <inheritdoc cref="ICacheMonitor"/>
        public void TrackMemoryAllocated(int memoryInByte)
        {
            this.Logger.TrackMetric("MemoryAllocatedInByte", memoryInByte, this.LogProperties);
        }

        /// <inheritdoc cref="ICacheMonitor"/>
        public void TrackMemoryReleased(int memoryInByte)
        {
            this.Logger.TrackMetric("MemoryReleasedInByte", memoryInByte, this.LogProperties);
        }

        /// <inheritdoc cref="ICacheMonitor"/>
        public void TrackMessagesAdded(long mesageAdded)
        {
            this.Logger.TrackMetric("MessageAdded", mesageAdded, this.LogProperties);
        }

        /// <inheritdoc cref="ICacheMonitor"/>
        public void TrackMessagesPurged(long messagePurged)
        {
            this.Logger.TrackMetric("MessagePurged", messagePurged, this.LogProperties);
        }
    }
}
