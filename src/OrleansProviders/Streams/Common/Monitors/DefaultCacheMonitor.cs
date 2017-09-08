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
        protected readonly IMetricsWriter MetricsWriter;
        protected Dictionary<string, string> LogProperties;

        public DefaultCacheMonitor(IMetricsWriter metricsWriter)
        {
            this.MetricsWriter = metricsWriter;
        }

        public DefaultCacheMonitor(CacheMonitorDimensions dimensions, IMetricsWriter metricsWriter)
            :this(metricsWriter)
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
            this.MetricsWriter.TrackMetric($"{pressureMonitorType}-UnderPressure", underPressure ? 1 : 0, this.LogProperties);
            if (cachePressureContributionCount.HasValue)
                this.MetricsWriter.TrackMetric($"{pressureMonitorType}-PressureContributionCount", cachePressureContributionCount.Value, this.LogProperties);
            if (currentPressure.HasValue)
                this.MetricsWriter.TrackMetric($"{pressureMonitorType}-CurrentPressure", currentPressure.Value, this.LogProperties);
        }

        /// <inheritdoc cref="ICacheMonitor"/>
        public void ReportCacheSize(long totalCacheSizeInByte)
        {
            this.MetricsWriter.TrackMetric("TotalCacheSizeInByte", totalCacheSizeInByte, this.LogProperties);
        }

        /// <inheritdoc cref="ICacheMonitor"/>
        public void ReportMessageStatistics(DateTime? oldestMessageEnqueueTimeUtc, DateTime? oldestMessageDequeueTimeUtc, DateTime? newestMessageEnqueueTimeUtc, long totalMessageCount)
        {
            if (oldestMessageEnqueueTimeUtc.HasValue && newestMessageEnqueueTimeUtc.HasValue)
                this.MetricsWriter.TrackMetric("OldestMessageRelativeAgeToNewestMessage", newestMessageEnqueueTimeUtc.Value - oldestMessageEnqueueTimeUtc.Value, this.LogProperties);

            if (oldestMessageDequeueTimeUtc.HasValue)
                this.MetricsWriter.TrackMetric("OldestMessageDequeueTimeToNow", DateTime.UtcNow - oldestMessageDequeueTimeUtc.Value, this.LogProperties);

            this.MetricsWriter.TrackMetric("TotalMessageCount", totalMessageCount, this.LogProperties);
        }

        /// <inheritdoc cref="ICacheMonitor"/>
        public void TrackMemoryAllocated(int memoryInByte)
        {
            this.MetricsWriter.TrackMetric("MemoryAllocatedInByte", memoryInByte, this.LogProperties);
        }

        /// <inheritdoc cref="ICacheMonitor"/>
        public void TrackMemoryReleased(int memoryInByte)
        {
            this.MetricsWriter.TrackMetric("MemoryReleasedInByte", memoryInByte, this.LogProperties);
        }

        /// <inheritdoc cref="ICacheMonitor"/>
        public void TrackMessagesAdded(long mesageAdded)
        {
            this.MetricsWriter.TrackMetric("MessageAdded", mesageAdded, this.LogProperties);
        }

        /// <inheritdoc cref="ICacheMonitor"/>
        public void TrackMessagesPurged(long messagePurged)
        {
            this.MetricsWriter.TrackMetric("MessagePurged", messagePurged, this.LogProperties);
        }
    }
}
