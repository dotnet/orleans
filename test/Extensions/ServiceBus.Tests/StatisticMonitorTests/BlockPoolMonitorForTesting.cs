using Orleans.Providers.Streams.Common;

namespace ServiceBus.Tests.MonitorTests
{
    public class BlockPoolMonitorForTesting : IBlockPoolMonitor
    {
        public ObjectPoolMonitorCounters CallCounters { get; } = new ObjectPoolMonitorCounters();
 
        public void TrackMemoryAllocated(long allocatedMemoryInByte)
        {
            Interlocked.Increment(ref CallCounters.TrackObjectAllocatedByCacheCallCounter);
        }

        public void TrackMemoryReleased(long releasedMemoryInByte)
        {
            Interlocked.Increment(ref CallCounters.TrackObjectReleasedFromCacheCallCounter);
        }

        public void Report(long totalMemoryInByte, long availableMemoryInByte, long claimedMemoryInByte)
        {
            Interlocked.Increment(ref CallCounters.ReportCallCounter);
        }
    }

    [Serializable]
    [Orleans.GenerateSerializer]
    public class ObjectPoolMonitorCounters
    {
        [Orleans.Id(0)]
        public int TrackObjectAllocatedByCacheCallCounter;
        [Orleans.Id(1)]
        public int TrackObjectReleasedFromCacheCallCounter;
        [Orleans.Id(2)]
        public int ReportCallCounter;
    }
}
