using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Responsible for monitoring cache related metrics
    /// </summary>
    public interface ICacheMonitor
    {
        #region Event driven metrics

        /// <summary>
        /// Track cache pressure metrics when cache pressure monitor encounter a status change
        /// </summary>
        void TrackCachePressureMonitorStatusChange(string pressureMonitorType, bool underPressure, double? cachePressureContributionCount, double? currentPressure);

        /// <summary>
        /// Track message added to the cache, and newest and oldest messsges among them 
        /// </summary>
        /// <param name="messageAdded"></param>
        void TrackMessageAdded(long messageAdded);

        /// <summary>
        /// Track message purged from the cache, and the newest and oldest messages among them
        /// </summary>
        /// <param name="messagePurged"></param>
        void TrackMessagePurged(long messagePurged);

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

        #endregion

        #region periodically report discrete metrics

        /// <summary>
        /// Periodically report cache status metrics
        /// </summary>
        /// <param name="oldestMessageAge">Relative age between oldest message and newest message in cache</param>
        /// <param name="oldestMessageEnqueueTimeToNow">Difference between host utcNow and oldest message's enqueue time in cache </param>
        /// <param name="newestMessageEnqueueTimeToNow">Difference between host utcNow and newest message enqueue time in cahce</param>
        /// <param name="totalMessageCount"></param>
        void ReportMessageStatistics(TimeSpan? oldestMessageAge, TimeSpan? oldestMessageEnqueueTimeToNow, TimeSpan? newestMessageEnqueueTimeToNow, 
            long totalMessageCount);

        /// <summary>
        /// report total cache size
        /// </summary>
        /// <param name="totalCacheSizeInByte"></param>
        void ReportCacheSize(long totalCacheSizeInByte);

        #endregion
    }
}
