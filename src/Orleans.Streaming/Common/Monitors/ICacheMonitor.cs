using System;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Responsible for monitoring cache related metrics.
    /// </summary>
    public interface ICacheMonitor
    {
        /// <summary>
        /// Called when the cache pressure monitor encounter a status change.
        /// </summary>
        /// <param name="pressureMonitorType">Type of the pressure monitor.</param>
        /// <param name="underPressure">if set to <see langword="true" />, the cache is under pressure.</param>
        /// <param name="cachePressureContributionCount">The cache pressure contribution count.</param>
        /// <param name="currentPressure">The current pressure.</param>
        /// <param name="flowControlThreshold">The flow control threshold.</param>
        void TrackCachePressureMonitorStatusChange(string pressureMonitorType, bool underPressure, double? cachePressureContributionCount, double? currentPressure, 
            double? flowControlThreshold);

        /// <summary>
        /// Called when messages are added to the cache.
        /// </summary>
        /// <param name="messagesAdded">The number of messages added.</param>
        void TrackMessagesAdded(long messagesAdded);

        /// <summary>
        /// Called when messages are purged from the cache.
        /// </summary>
        /// <param name="messagesPurged">The number of messages purged.</param>
        void TrackMessagesPurged(long messagesPurged);

        /// <summary>
        /// Called when new memory is allocated by the cache.
        /// </summary>
        /// <param name="memoryInBytes">The memory in bytes.</param>
        void TrackMemoryAllocated(int memoryInBytes);

        /// <summary>
        /// Called when memory returned to block pool.
        /// </summary>
        /// <param name="memoryInBytes">The memory in bytes.</param>
        void TrackMemoryReleased(int memoryInBytes);

        /// <summary>
        /// Called to report cache status metrics.
        /// </summary>
        /// <param name="oldestMessageEnqueueTimeUtc">The time in UTC when the oldest message was enqueued to the queue.</param>
        /// <param name="oldestMessageDequeueTimeUtc">The time in UTC when the oldest message was read from the queue and put in the cache.</param>
        /// <param name="newestMessageEnqueueTimeUtc">The time in UTC when the newest message was enqueued to the queue.</param>
        /// <param name="totalMessageCount">The total message count.</param>
        void ReportMessageStatistics(DateTime? oldestMessageEnqueueTimeUtc, DateTime? oldestMessageDequeueTimeUtc, DateTime? newestMessageEnqueueTimeUtc, 
            long totalMessageCount);

        /// <summary>
        /// Called to report the total cache size.
        /// </summary>
        /// <param name="totalCacheSizeInBytes">The total cache size in bytes.</param>
        void ReportCacheSize(long totalCacheSizeInBytes);
    }
}
