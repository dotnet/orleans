using Orleans.Runtime;
using System;
using System.Collections.Generic;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// cache monitor used as a default option in GeneratorStreamprovider and MemoryStreamProvider
    /// </summary>
    public class DefaultCacheMonitor : ICacheMonitor
    {
        protected readonly ITelemetryProducer TelemetryProducer;
        protected Dictionary<string, string> LogProperties;

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultCacheMonitor"/> class.
        /// </summary>
        /// <param name="telemetryProducer">The telemetry producer.</param>
        public DefaultCacheMonitor(ITelemetryProducer telemetryProducer)
        {
            this.TelemetryProducer = telemetryProducer;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultCacheMonitor"/> class.
        /// </summary>
        /// <param name="dimensions">The dimensions.</param>
        /// <param name="telemetryProducer">The telemetry producer.</param>
        public DefaultCacheMonitor(CacheMonitorDimensions dimensions, ITelemetryProducer telemetryProducer)
            : this(telemetryProducer)
        {
            this.LogProperties = new Dictionary<string, string>
            {
                {"QueueId", dimensions.QueueId},
            };
        }

        /// <inheritdoc />
        public void TrackCachePressureMonitorStatusChange(string pressureMonitorType, bool underPressure, double? cachePressureContributionCount, double? currentPressure,
            double? flowControlThreshold)
        {
            this.TelemetryProducer.TrackMetric($"{pressureMonitorType}-UnderPressure", underPressure ? 1 : 0, this.LogProperties);
            if (cachePressureContributionCount.HasValue)
                this.TelemetryProducer.TrackMetric($"{pressureMonitorType}-PressureContributionCount", cachePressureContributionCount.Value, this.LogProperties);
            if (currentPressure.HasValue)
                this.TelemetryProducer.TrackMetric($"{pressureMonitorType}-CurrentPressure", currentPressure.Value, this.LogProperties);
        }

        /// <inheritdoc />
        public void ReportCacheSize(long totalCacheSizeInByte)
        {
            this.TelemetryProducer.TrackMetric("TotalCacheSizeInByte", totalCacheSizeInByte, this.LogProperties);
        }

        /// <inheritdoc />
        public void ReportMessageStatistics(DateTime? oldestMessageEnqueueTimeUtc, DateTime? oldestMessageDequeueTimeUtc, DateTime? newestMessageEnqueueTimeUtc, long totalMessageCount)
        {
            if (oldestMessageEnqueueTimeUtc.HasValue && newestMessageEnqueueTimeUtc.HasValue)
                this.TelemetryProducer.TrackMetric("OldestMessageRelativeAgeToNewestMessage", newestMessageEnqueueTimeUtc.Value - oldestMessageEnqueueTimeUtc.Value, this.LogProperties);

            if (oldestMessageDequeueTimeUtc.HasValue)
                this.TelemetryProducer.TrackMetric("OldestMessageDequeueTimeToNow", DateTime.UtcNow - oldestMessageDequeueTimeUtc.Value, this.LogProperties);

            this.TelemetryProducer.TrackMetric("TotalMessageCount", totalMessageCount, this.LogProperties);
        }

        /// <inheritdoc />
        public void TrackMemoryAllocated(int memoryInByte)
        {
            this.TelemetryProducer.TrackMetric("MemoryAllocatedInByte", memoryInByte, this.LogProperties);
        }

        /// <inheritdoc />
        public void TrackMemoryReleased(int memoryInByte)
        {
            this.TelemetryProducer.TrackMetric("MemoryReleasedInByte", memoryInByte, this.LogProperties);
        }

        /// <inheritdoc />
        public void TrackMessagesAdded(long mesageAdded)
        {
            this.TelemetryProducer.TrackMetric("MessageAdded", mesageAdded, this.LogProperties);
        }

        /// <inheritdoc />
        public void TrackMessagesPurged(long messagePurged)
        {
            this.TelemetryProducer.TrackMetric("MessagePurged", messagePurged, this.LogProperties);
        }
    }
}
