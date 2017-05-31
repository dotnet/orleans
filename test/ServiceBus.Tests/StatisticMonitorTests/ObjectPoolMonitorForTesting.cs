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
    public class ObjectPoolMonitorForTesting : IObjectPoolMonitor
    {
        public static ObjectPoolMonitorForTesting Instance = new ObjectPoolMonitorForTesting(null, null);
        public ObjectPoolMonitorCounters CallCounters;
        private ObjectPoolMonitorForTesting(EventHubObjectPoolMonitorDimentions dimentions, Logger logger)
        {
            CallCounters = new ObjectPoolMonitorCounters();
        }
 
        public void TrackObjectAllocatedByCache(int blockCount)
        {
            Interlocked.Increment(ref this.CallCounters.TrackObjectAllocatedByCacheCallCounter);
        }

        public void TrackObjectReleasedFromCache(int blockCount)
        {
            Interlocked.Increment(ref this.CallCounters.TrackObjectReleasedFromCacheCallCounter);
        }

        public void Report(long totalBlocks, long freeBlocks, long claimedBlocks)
        {
            Interlocked.Increment(ref this.CallCounters.ReportCallCounter);
        }
    }
    [Serializable]
    public class ObjectPoolMonitorCounters
    {
        public int TrackObjectAllocatedByCacheCallCounter = 0;
        public int TrackObjectReleasedFromCacheCallCounter = 0;
        public int ReportCallCounter = 0;
    }
}
