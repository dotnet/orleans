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
    public class BlockPoolMonitorForTesting : IBlockPoolMonitor
    {
        public ObjectPoolMonitorCounters CallCounters { get; } = new ObjectPoolMonitorCounters();
 
        public void TrackMemoryAllocated(long allocatedMemoryInByte)
        {
            Interlocked.Increment(ref this.CallCounters.TrackObjectAllocatedByCacheCallCounter);
        }

        public void TrackMemoryReleased(long releasedMemoryInByte)
        {
            Interlocked.Increment(ref this.CallCounters.TrackObjectReleasedFromCacheCallCounter);
        }

        public void Report(long totalMemoryInByte, long availableMemoryInByte, long claimedMemoryInByte)
        {
            Interlocked.Increment(ref this.CallCounters.ReportCallCounter);
        }
    }

    [Serializable]
    public class ObjectPoolMonitorCounters
    {
        public int TrackObjectAllocatedByCacheCallCounter;
        public int TrackObjectReleasedFromCacheCallCounter;
        public int ReportCallCounter;
    }
}
