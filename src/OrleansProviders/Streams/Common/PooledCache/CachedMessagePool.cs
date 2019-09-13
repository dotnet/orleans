
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
        public CachedMessagePool()
        {
            messagePool = new ObjectPool<CachedMessageBlock>(
                () => new CachedMessageBlock());
        }

        /// <summary>
        /// Allocates a message in a block and returns the block the message is in.
        /// </summary>
        /// <returns></returns>
        public CachedMessageBlock AllocateMessage(in CachedMessage message)
        {
            CachedMessageBlock returnBlock = this.currentMessageBlock ?? (this.currentMessageBlock = this.messagePool.Allocate());
            returnBlock.Add(message);

            // blocks at capacity are eligable for purge, so we don't want to be holding on to them.
            if (!this.currentMessageBlock.HasCapacity)
            {
                this.currentMessageBlock = this.messagePool.Allocate();
            }

            return returnBlock;
        }
    }
}
