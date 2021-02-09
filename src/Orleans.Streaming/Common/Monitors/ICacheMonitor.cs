using System;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Responsible for monitoring cache related metrics
    /// </summary>
    public interface ICacheMonitor
    {
        /// <summary>
        /// Track cache pressure metrics when cache pressure monitor encounter a status change
        /// </summary>
        /// <param name="pressureMonitorType"></param>
        /// <param name="underPressure"></param>
        /// <param name="cachePressureContributionCount"></param>
        /// <param name="currentPressure"></param>
        /// <param name="flowControlThreshold"></param>
        void TrackCachePressureMonitorStatusChange(string pressureMonitorType, bool underPressure, double? cachePressureContributionCount, double? currentPressure, 
            double? flowControlThreshold);

        /// <summary>
        /// Track message added to the cache, and newest and oldest messages among them 
        /// </summary>
        /// <param name="messageAdded"></param>
        void TrackMessagesAdded(long messageAdded);

        /// <summary>
        /// Track message purged from the cache, and the newest and oldest messages among them
        /// </summary>
        /// <param name="messagePurged"></param>
        void TrackMessagesPurged(long messagePurged);

        /// <summary>
        /// Track new memory allocated by the cache
        /// </summary>
        /// <param name="memoryInByte"></param>
        void TrackMemoryAllocated(int memoryInByte);

        /// <summary>
        /// Track memory returned to block pool
        /// </summary>
        /// <param name="memoryInByte"></param>
        void TrackMemoryReleased(int memoryInByte);

        /// <summary>
        /// Periodically report cache status metrics
        /// </summary>
        /// <param name="oldestMessageEnqueueTimeUtc">The time in Utc when oldest message enqueued the queue.</param>
        /// <param name="oldestMessageDequeueTimeUtc">The time in Utc when oldest message was read from the queue and put in cache.</param>
        /// <param name="newestMessageEnqueueTimeUtc">The time in Utc when newest message enqueued the queue.</param>
        /// <param name="totalMessageCount"></param>
        void ReportMessageStatistics(DateTime? oldestMessageEnqueueTimeUtc, DateTime? oldestMessageDequeueTimeUtc, DateTime? newestMessageEnqueueTimeUtc, 
            long totalMessageCount);

        /// <summary>
        /// report total cache size
        /// </summary>
        /// <param name="totalCacheSizeInByte"></param>
        void ReportCacheSize(long totalCacheSizeInByte);
    }
}
