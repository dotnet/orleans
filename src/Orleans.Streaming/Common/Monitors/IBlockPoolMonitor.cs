namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Monitor track block pool related metrics. Block pool is used in cache system for memory management 
    /// </summary>
    public interface IBlockPoolMonitor
    {
        /// <summary>
        /// Track memory newly allocated by cache
        /// </summary>
        /// <param name="allocatedMemoryInByte"></param>
        void TrackMemoryAllocated(long allocatedMemoryInByte);

        /// <summary>
        /// Track memory released from cache
        /// </summary>
        /// <param name="releasedMemoryInByte"></param>
        void TrackMemoryReleased(long releasedMemoryInByte);

        /// <summary>
        /// Periodically report block pool status
        /// </summary>
        /// <param name="totalSizeInByte">total memory this block pool allocated</param>
        /// <param name="availableMemoryInByte">memory which is available for allocating to caches</param>
        /// <param name="claimedMemoryInByte">memory is in use by caches</param>
        void Report(long totalSizeInByte, long availableMemoryInByte, long claimedMemoryInByte);
    }

    /// <summary>
    /// ObjectPoolMonitor report metrics for ObjectPool, which are based on object count. BlockPoolMonitor report metrics for BlockPool, which are based on memory size. 
    /// These two monitor converge in orleans cache infrastructure, where ObjectPool is used as block pool to allocate memory, where each object represent a block of memory
    /// which has a size. ObjectPoolMonitorBridge is the bridge between these two monitors in cache infrastructure. When ObjectPoolMonitor is reporting a metric, 
    /// the user configured BlockPoolMonitor will call its counterpart method and reporting metric based on the math: memoryInByte = objectCount*objectSizeInByte
    /// </summary>
    public class ObjectPoolMonitorBridge : IObjectPoolMonitor
    {
        private IBlockPoolMonitor blockPoolMonitor;
        private int blockSizeInByte;
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="blockPoolMonitor"></param>
        /// <param name="blockSizeInByte"></param>
        public ObjectPoolMonitorBridge(IBlockPoolMonitor blockPoolMonitor, int blockSizeInByte)
        {
            this.blockPoolMonitor = blockPoolMonitor;
            this.blockSizeInByte = blockSizeInByte;
        }

        /// <summary>
        /// Track object allocated event and also call its blcokPoolMonitor to report TrackMemoryAllocatedByCache
        /// </summary>
        public void TrackObjectAllocated()
        {
            long memoryAllocatedInByte = blockSizeInByte;
            this.blockPoolMonitor.TrackMemoryAllocated(memoryAllocatedInByte);
        }

        /// <summary>
        /// Track object released, and also call its blockPoolMonitor to report TrackMemoryReleasedFromCache
        /// </summary>
        public void TrackObjectReleased()
        {
            long memoryReleasedInByte = blockSizeInByte;
            this.blockPoolMonitor.TrackMemoryReleased(memoryReleasedInByte);
        }

        /// <summary>
        /// Periodically report object pool status, and also call its blockPoolMonitor to report its counter part metrics 
        /// </summary>
        /// <param name="totalObjects"></param>
        /// <param name="availableObjects"></param>
        /// <param name="claimedObjects"></param>
        public void Report(long totalObjects, long availableObjects, long claimedObjects)
        {
            var totalMemoryInByte = totalObjects * this.blockSizeInByte;
            var availableMemoryInByte = availableObjects * this.blockSizeInByte;
            var claimedMemoryInByte = claimedObjects * this.blockSizeInByte;
            this.blockPoolMonitor.Report(totalMemoryInByte, availableMemoryInByte, claimedMemoryInByte);
        }
    }
}
