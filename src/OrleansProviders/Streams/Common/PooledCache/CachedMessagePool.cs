
using System;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Pool of tightly packed cached messages that are kept in large blocks to reduce GC pressure.
    /// </summary>
    /// <typeparam name="TQueueMessage"></typeparam>
    /// <typeparam name="TCachedMessage">Tightly packed structure.  Struct should contain only value types.</typeparam>
    internal class CachedMessagePool<TQueueMessage, TCachedMessage>
        where TQueueMessage : class
        where TCachedMessage : struct
    {
        private readonly IObjectPool<CachedMessageBlock<TQueueMessage, TCachedMessage>> cachedMessagePool;
        private CachedMessageBlock<TQueueMessage, TCachedMessage> cachedMessageBlock;

        /// <summary>
        /// Allocates a pool of cached message blocks.
        /// </summary>
        /// <param name="cacheDataAdapter"></param>
        public CachedMessagePool(ICacheDataAdapter<TQueueMessage, TCachedMessage> cacheDataAdapter)
        {
            cachedMessagePool = new ObjectPool<CachedMessageBlock<TQueueMessage, TCachedMessage>>(pool => new CachedMessageBlock<TQueueMessage, TCachedMessage>(pool, cacheDataAdapter));
        }

        /// <summary>
        /// Allocates a message in a block and returns the block the message is in.
        /// </summary>
        /// <param name="queueMessage"></param>
        /// <returns></returns>
        public CachedMessageBlock<TQueueMessage, TCachedMessage> AllocateMessage(TQueueMessage queueMessage)
        {
            if (queueMessage == null)
            {
                throw new ArgumentNullException("queueMessage");
            }

            // allocate new cached message block if needed
            if (cachedMessageBlock == null || !cachedMessageBlock.HasCapacity)
            {
                cachedMessageBlock = cachedMessagePool.Allocate();
            }

            cachedMessageBlock.Add(queueMessage);

            return cachedMessageBlock;
        }
    }
}
