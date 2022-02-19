
namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Pool of tightly packed cached messages that are kept in large blocks to reduce GC pressure.
    /// </summary>
    internal class CachedMessagePool
    {
        private readonly IObjectPool<CachedMessageBlock> messagePool;
        private CachedMessageBlock currentMessageBlock;

        /// <summary>
        /// Allocates a pool of cached message blocks.
        /// </summary>
        /// <param name="cacheDataAdapter">The cache data adapter.</param>
        public CachedMessagePool(ICacheDataAdapter cacheDataAdapter)
        {
            messagePool = new ObjectPool<CachedMessageBlock>(
                () => new CachedMessageBlock());
        }

        /// <summary>
        /// Allocates a message in a block and returns the block the message is in.
        /// </summary>
        /// <returns>The cached message block which the message was allocated in.</returns>
        public CachedMessageBlock AllocateMessage(CachedMessage message)
        {
            CachedMessageBlock returnBlock = currentMessageBlock ?? (currentMessageBlock = messagePool.Allocate());
            returnBlock.Add(message);

            // blocks at capacity are eligable for purge, so we don't want to be holding on to them.
            if (!currentMessageBlock.HasCapacity)
            {
                currentMessageBlock = messagePool.Allocate();
            }

            return returnBlock;
        }
    }
}
