
using System;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Pool of tightly packed cached messages that are kept in large blocks to reduce GC pressure.
    /// </summary>
    /// <typeparam name="TQueueMessage"></typeparam>
    /// <typeparam name="TCachedMessage">Tightly packed structure.  Struct should contain only value types.</typeparam>
    internal class CachedMessagePool<TQueueMessage, TCachedMessage>
        where TCachedMessage : struct
    {
        private readonly ICacheDataAdapter<TQueueMessage, TCachedMessage> dataAdapter;
        private readonly IObjectPool<CachedMessageBlock<TCachedMessage>> messagePool;
        private CachedMessageBlock<TCachedMessage> currentMessageBlock;

        /// <summary>
        /// Allocates a pool of cached message blocks.
        /// </summary>
        /// <param name="cacheDataAdapter"></param>
        public CachedMessagePool(ICacheDataAdapter<TQueueMessage, TCachedMessage> cacheDataAdapter)
        {
            if (cacheDataAdapter == null)
            {
                throw new ArgumentNullException(nameof(cacheDataAdapter));
            }
            dataAdapter = cacheDataAdapter;
            messagePool = new ObjectPool<CachedMessageBlock<TCachedMessage>>(
                () => new CachedMessageBlock<TCachedMessage>());
        }

        /// <summary>
        /// Allocates a message in a block and returns the block the message is in.
        /// </summary>
        /// <param name="queueMessage"></param>
        /// <param name="dequeueTimeUtc"></param>
        /// <param name="streamPosition"></param>
        /// <returns></returns>
        public CachedMessageBlock<TCachedMessage> AllocateMessage(TQueueMessage queueMessage, DateTime dequeueTimeUtc, out StreamPosition streamPosition)
        {
            streamPosition = default(StreamPosition);
            if (queueMessage == null)
            {
                throw new ArgumentNullException("queueMessage");
            }

            // allocate new cached message block if needed
            if (currentMessageBlock == null || !currentMessageBlock.HasCapacity)
            {
                currentMessageBlock = messagePool.Allocate();
            }

            streamPosition = currentMessageBlock.Add(queueMessage, dequeueTimeUtc, dataAdapter);

            return currentMessageBlock;
        }
    }
}
