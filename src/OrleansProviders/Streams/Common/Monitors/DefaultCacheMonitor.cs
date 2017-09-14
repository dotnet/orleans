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
        protected readonly ITelemetryClient TelemetryClient;
        protected Dictionary<string, string> LogProperties;

        public DefaultCacheMonitor(ITelemetryClient telemetryClient)
        {
            this.TelemetryClient = telemetryClient;
        }

        public DefaultCacheMonitor(CacheMonitorDimensions dimensions, ITelemetryClient telemetryClient)
            :this(telemetryClient)
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
            this.TelemetryClient.TrackMetric($"{pressureMonitorType}-UnderPressure", underPressure ? 1 : 0, this.LogProperties);
            if (cachePressureContributionCount.HasValue)
                this.TelemetryClient.TrackMetric($"{pressureMonitorType}-PressureContributionCount", cachePressureContributionCount.Value, this.LogProperties);
            if (currentPressure.HasValue)
                this.TelemetryClient.TrackMetric($"{pressureMonitorType}-CurrentPressure", currentPressure.Value, this.LogProperties);
        }

        /// <inheritdoc cref="ICacheMonitor"/>
        public void ReportCacheSize(long totalCacheSizeInByte)
        {
            this.TelemetryClient.TrackMetric("TotalCacheSizeInByte", totalCacheSizeInByte, this.LogProperties);
        }

        /// <inheritdoc cref="ICacheMonitor"/>
        public void ReportMessageStatistics(DateTime? oldestMessageEnqueueTimeUtc, DateTime? oldestMessageDequeueTimeUtc, DateTime? newestMessageEnqueueTimeUtc, long totalMessageCount)
        {
            if (oldestMessageEnqueueTimeUtc.HasValue && newestMessageEnqueueTimeUtc.HasValue)
                this.TelemetryClient.TrackMetric("OldestMessageRelativeAgeToNewestMessage", newestMessageEnqueueTimeUtc.Value - oldestMessageEnqueueTimeUtc.Value, this.LogProperties);

            if (oldestMessageDequeueTimeUtc.HasValue)
                this.TelemetryClient.TrackMetric("OldestMessageDequeueTimeToNow", DateTime.UtcNow - oldestMessageDequeueTimeUtc.Value, this.LogProperties);

            this.TelemetryClient.TrackMetric("TotalMessageCount", totalMessageCount, this.LogProperties);
        }

        /// <inheritdoc cref="ICacheMonitor"/>
        public void TrackMemoryAllocated(int memoryInByte)
        {
            this.TelemetryClient.TrackMetric("MemoryAllocatedInByte", memoryInByte, this.LogProperties);
        }

        /// <inheritdoc cref="ICacheMonitor"/>
        public void TrackMemoryReleased(int memoryInByte)
        {
            this.TelemetryClient.TrackMetric("MemoryReleasedInByte", memoryInByte, this.LogProperties);
        }

        /// <inheritdoc cref="ICacheMonitor"/>
        public void TrackMessagesAdded(long mesageAdded)
        {
            this.TelemetryClient.TrackMetric("MessageAdded", mesageAdded, this.LogProperties);
        }

        /// <inheritdoc cref="ICacheMonitor"/>
        public void TrackMessagesPurged(long messagePurged)
        {
            this.TelemetryClient.TrackMetric("MessagePurged", messagePurged, this.LogProperties);
        }
    }
}
