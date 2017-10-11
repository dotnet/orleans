
using System;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Pool of tightly packed cached messages that are kept in large blocks to reduce GC pressure.
    /// </summary>
    /// <typeparam name="TQueueMessage">Type of message read from the queue</typeparam>
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
            dataAdapter = cacheDataAdapter ?? throw new ArgumentNullException(nameof(cacheDataAdapter));
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
            if (queueMessage == null) throw new ArgumentNullException(nameof(queueMessage));

            CachedMessageBlock<TCachedMessage> returnBlock = currentMessageBlock ?? (currentMessageBlock = messagePool.Allocate());
            streamPosition = returnBlock.Add(queueMessage, dequeueTimeUtc, dataAdapter);

            // blocks at capacity are eligable for purge, so we don't want to be holding on to them.
            if (!currentMessageBlock.HasCapacity)
            {
                currentMessageBlock = messagePool.Allocate();
            }

            return returnBlock;
        }
    }
}
