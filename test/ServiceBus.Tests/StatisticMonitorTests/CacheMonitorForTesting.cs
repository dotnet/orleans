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
        private Logger logger;

        private CacheMonitorForTesting(EventHubCacheMonitorDimensions dimensions, Logger logger)
        {
            this.CallCounters = new CacheMonitorCounters();
            this.logger = logger;
        }

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
