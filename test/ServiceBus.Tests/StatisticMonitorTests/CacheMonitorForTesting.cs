using Orleans;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.ServiceBus.Providers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;

namespace ServiceBus.Tests.MonitorTests
{
    public class CacheMonitorForTesting : ICacheMonitor
    {
        public static CacheMonitorForTesting Instance = new CacheMonitorForTesting(null, null);
        public CacheMonitorCounters CallCounters;

        private CacheMonitorForTesting(EventHubCacheMonitorDimentions dimentions, Logger logger)
        {
            this.CallCounters = new CacheMonitorCounters();
        }

        public void TrackCachePressureMonitorStatusChange(string pressureMonitorType, bool underPressure, double? cachePressureContributionCount, double? currentPressure)
        {
            Interlocked.Increment(ref CallCounters.TrackCachePressureMonitorStatusChangeCallCounter);
        }

        public void ReportCacheSize(long totalCacheSizeInByte)
        {
            Interlocked.Increment(ref CallCounters.ReportCacheSizeCallCounter);
        }

        public void ReportMessageStatistics(TimeSpan? oldestMessageAge, TimeSpan? oldestMessageEnqueueTimeToNow, TimeSpan? newestMessageEnqueueTimeToNow, long totalMessageCount)
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

        public void TrackMessageAdded(long mesageAdded)
        {
            Interlocked.Increment(ref CallCounters.TrackMessageAddedCounter);
        }

        public void TrackMessagePurged(long messagePurged)
        {
            Interlocked.Increment(ref CallCounters.TrackMessagePurgedCounter);
        }
    }

    [Serializable]
    public class CacheMonitorCounters
    {
        public int TrackCachePressureMonitorStatusChangeCallCounter = 0;
        public int ReportCacheSizeCallCounter = 0;
        public int ReportMessageStatisticsCallCounter = 0;
        public int TrackMemoryAllocatedCallCounter = 0;
        public int TrackMemoryReleasedCallCounter = 0;
        public int TrackMessageAddedCounter = 0;
        public int TrackMessagePurgedCounter = 0;
    }
}
