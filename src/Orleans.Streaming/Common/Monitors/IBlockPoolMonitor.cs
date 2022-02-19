namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Monitor track block pool related metrics. Block pool is used in cache system for memory management 
    /// </summary>
    public interface IBlockPoolMonitor
    {
        /// <summary>
        /// Called when memory is newly allocated by the cache.
        /// </summary>
        /// <param name="allocatedMemoryInBytes">The allocated memory, in bytes.</param>
        void TrackMemoryAllocated(long allocatedMemoryInBytes);

        /// <summary>
        /// Called when memory is released by the cache.
        /// </summary>
        /// <param name="releasedMemoryInBytes">The released memory, in bytes.</param>
        void TrackMemoryReleased(long releasedMemoryInBytes);

        /// <summary>
        /// Periodically report block pool status
        /// </summary>
        /// <param name="totalSizeInByte">Total memory this block pool allocated.</param>
        /// <param name="availableMemoryInByte">Memory which is available for allocating to caches.</param>
        /// <param name="claimedMemoryInByte">Memory in use by caches.</param>
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
        private int blockSizeInBytes;

        /// <summary>
        /// Initializes a new instance of the <see cref="ObjectPoolMonitorBridge"/> class.
        /// </summary>
        /// <param name="blockPoolMonitor">The block pool monitor.</param>
        /// <param name="blockSizeInBytes">The block size in bytes.</param>
        public ObjectPoolMonitorBridge(IBlockPoolMonitor blockPoolMonitor, int blockSizeInBytes)
        {
            this.blockPoolMonitor = blockPoolMonitor;
            this.blockSizeInBytes = blockSizeInBytes;
        }

        /// <inheritdoc />
        public void TrackObjectAllocated()
        {
            long memoryAllocatedInByte = blockSizeInBytes;
            this.blockPoolMonitor.TrackMemoryAllocated(memoryAllocatedInByte);
        }

        /// <inheritdoc />
        public void TrackObjectReleased()
        {
            long memoryReleasedInByte = blockSizeInBytes;
            this.blockPoolMonitor.TrackMemoryReleased(memoryReleasedInByte);
        }

        /// <inheritdoc />
        public void Report(long totalObjects, long availableObjects, long claimedObjects)
        {
            var totalMemoryInByte = totalObjects * this.blockSizeInBytes;
            var availableMemoryInByte = availableObjects * this.blockSizeInBytes;
            var claimedMemoryInByte = claimedObjects * this.blockSizeInBytes;
            this.blockPoolMonitor.Report(totalMemoryInByte, availableMemoryInByte, claimedMemoryInByte);
        }
    }
}
