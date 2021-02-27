using Orleans.Providers.Streams.Common;
using System;
using System.Threading;

namespace ServiceBus.Tests.MonitorTests
{
    public class CacheMonitorForTesting : ICacheMonitor
    {
        public CacheMonitorCounters CallCounters { get; } = new CacheMonitorCounters();
        
        public void TrackCachePressureMonitorStatusChange(string pressureMonitorType, bool underPressure, double? cachePressureContributionCount, double? currentPressure,
            double? flowControlThreshold)
        {
            Interlocked.Increment(ref CallCounters.TrackCachePressureMonitorStatusChangeCallCounter);
        }

        public void ReportCacheSize(long totalCacheSizeInByte)
        {
            Interlocked.Increment(ref CallCounters.ReportCacheSizeCallCounter);
        }

        public void ReportMessageStatistics(DateTime? oldestMessageEnqueueTimeUtc, DateTime? oldestMessageDequeueTimeUtc, DateTime? newestMessageEnqueueTimeUtc, long totalMessageCount)
        {
            Interlocked.Increment(ref CallCounters.ReportMessageStatisticsCallCounter);
        }

        public void TrackMemoryAllocated(int memoryInByte)
        {
            Interlocked.Increment(ref CallCounters.TrackMemoryAllocatedCallCounter);
        }

        public void TrackMemoryReleased(int memoryInByte)
        {
            Interlocked.Increment(ref CallCounters.TrackMemoryReleasedCallCounter);
        }

        public void TrackMessagesAdded(long mesageAdded)
        {
            Interlocked.Increment(ref CallCounters.TrackMessageAddedCounter);
        }

        public void TrackMessagesPurged(long messagePurged)
        {
            Interlocked.Increment(ref CallCounters.TrackMessagePurgedCounter);
        }
    }

    [Serializable]
    [Orleans.GenerateSerializer]
    public class CacheMonitorCounters
    {
        [Orleans.Id(0)]
        public int TrackCachePressureMonitorStatusChangeCallCounter;
        [Orleans.Id(1)]
        public int ReportCacheSizeCallCounter;
        [Orleans.Id(2)]
        public int ReportMessageStatisticsCallCounter;
        [Orleans.Id(3)]
        public int TrackMemoryAllocatedCallCounter;
        [Orleans.Id(4)]
        public int TrackMemoryReleasedCallCounter;
        [Orleans.Id(5)]
        public int TrackMessageAddedCounter;
        [Orleans.Id(6)]
        public int TrackMessagePurgedCounter;
    }
}
