
namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Pool of tightly packed cached messages that are kept in large blocks to reduce GC pressure.
    /// </summary>
    internal class CachedMessagePool
    {
        private readonly IObjectPool<CachedMessageBlock> messagePool;
        private CachedMessageBlock? currentMessageBlock;

        /// <summary>
        /// Allocates a pool of cached message blocks.
        /// </summary>
        /// <param name="cacheDataAdapter">The cache data adapter.</param>
        public CachedMessagePool(ICacheDataAdapter cacheDataAdapter)
            : this(cacheDataAdapter, 16 * 1024, 16 * 1024, int.MaxValue)
        {
        }

        public CachedMessagePool(
            ICacheDataAdapter cacheDataAdapter,
            int initialBlockSize,
            int maxBlockSize,
            int maxRetainedBlocks)
        {
            ArgumentNullException.ThrowIfNull(cacheDataAdapter);

            messagePool = new ObjectPool<CachedMessageBlock>(
                () => new CachedMessageBlock(initialBlockSize, maxBlockSize),
                maxRetainedBlocks);
        }

        /// <summary>
        /// Allocates a message in a block and returns the block the message is in.
        /// </summary>
        /// <returns>The cached message block which the message was allocated in.</returns>
        public CachedMessageBlock AllocateMessage(CachedMessage message, out int allocatedSizeDelta)
        {
            allocatedSizeDelta = 0;
            if (currentMessageBlock == null)
            {
                currentMessageBlock = messagePool.Allocate();
                allocatedSizeDelta = currentMessageBlock.AllocatedSizeInBytes;
            }

            CachedMessageBlock returnBlock = currentMessageBlock;
            var previousSize = returnBlock.AllocatedSizeInBytes;
            returnBlock.Add(message);
            allocatedSizeDelta += returnBlock.AllocatedSizeInBytes - previousSize;
            if (!currentMessageBlock.HasCapacity)
            {
                currentMessageBlock = null;
            }

            return returnBlock;
        }

        public void ReleaseCurrentBlock(CachedMessageBlock block)
        {
            if (!ReferenceEquals(currentMessageBlock, block))
            {
                throw new InvalidOperationException("The supplied block is not the current message block.");
            }

            currentMessageBlock = null;
            block.Dispose();
        }
    }
}
