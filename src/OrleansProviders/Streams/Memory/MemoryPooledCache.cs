
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Orleans.Providers.Abstractions;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace Orleans.Providers
{
    /// <summary>
    /// Pooled cache for memory stream provider
    /// </summary>
    public class MemoryPooledCache<TSerializer> : IQueueCache
        where TSerializer : class, IMemoryMessageBodySerializer
    {
        private static readonly byte[] offsetToken = new byte[0];

        private readonly IObjectPool<FixedSizeBuffer> bufferPool;
        private readonly TSerializer serializer;
        private readonly IFiFoEvictionStrategy<CachedMessage> evictionStrategy;
        private readonly PooledQueueCache cache;

        private FixedSizeBuffer currentBuffer;

        /// <summary>
        /// Pooled cache for memory stream provider
        /// </summary>
        /// <param name="bufferPool"></param>
        /// <param name="purgePredicate"></param>
        /// <param name="logger"></param>
        /// <param name="serializer"></param>
        /// <param name="cacheMonitor"></param>
        /// <param name="monitorWriteInterval">monitor write interval.  Only triggered for active caches.</param>
        public MemoryPooledCache(IObjectPool<FixedSizeBuffer> bufferPool, TimePurgePredicate purgePredicate, ILogger logger, TSerializer serializer, ICacheMonitor cacheMonitor, TimeSpan? monitorWriteInterval)
        {
            this.bufferPool = bufferPool;
            this.serializer = serializer;
            this.cache = new PooledQueueCache(cacheMonitor, monitorWriteInterval);
            this.evictionStrategy = new ChronologicalEvictionStrategy(purgePredicate, cacheMonitor, monitorWriteInterval);
        }

        private CachedMessage QueueMessageToCachedMessage(MemoryMessageData queueMessage, in DateTime dequeueTimeUtc)
        {
            StreamPosition streamPosition = this.GetStreamPosition(queueMessage);
            return CachedMessage.Create(
                streamPosition.SequenceToken.SequenceToken,
                StreamIdentityToken.Create(streamPosition.StreamIdentity),
                offsetToken,
                queueMessage.Payload.ToArray(),
                queueMessage.EnqueueTimeUtc,
                dequeueTimeUtc,
                this.GetSegment);
        }

        // Placed object message payload into a segment from a buffer pool.  When this get's too big, older blocks will be purged
        private ArraySegment<byte> GetSegment(int size)
        {
            // get segment from current block
            ArraySegment<byte> segment;
            if (this.currentBuffer == null || !this.currentBuffer.TryGetSegment(size, out segment))
            {
                // no block or block full, get new block and try again
                this.currentBuffer = this.bufferPool.Allocate();
                //call EvictionStrategy's OnBlockAllocated method
                this.evictionStrategy.OnBlockAllocated(this.currentBuffer);
                // if this fails with clean block, then requested size is too big
                if (!this.currentBuffer.TryGetSegment(size, out segment))
                {
                    string errmsg = $"Message size is too big. MessageSize: {size}";
                    throw new ArgumentOutOfRangeException(nameof(size), errmsg);
                }
            }
            return segment;
        }

        private StreamPosition GetStreamPosition(MemoryMessageData queueMessage)
        {
            return new StreamPosition(new StreamIdentity(queueMessage.StreamGuid, queueMessage.StreamNamespace),
                new EventSequenceTokenV2(queueMessage.SequenceNumber));
        }

        private class Cursor : IQueueCacheCursor
        {
            private readonly PooledQueueCache cache;
            private readonly TSerializer serializer;
            private readonly object cursor;
            private IBatchContainer current;

            public Cursor(PooledQueueCache cache, TSerializer serializer, IStreamIdentity streamIdentity,
                StreamSequenceToken token)
            {
                this.cache = cache;
                this.serializer = serializer;
                this.cursor = cache.GetCursor(StreamIdentityToken.Create(streamIdentity), token?.SequenceToken);
            }

            public void Dispose()
            {
            }

            public IBatchContainer GetCurrent(out Exception exception)
            {
                exception = null;
                return this.current;
            }

            public bool MoveNext()
            {
                if (!this.cache.TryGetNextMessage(cursor, out CachedMessage next))
                {
                    return false;
                }

                StreamIdentityToken streamIdentityToken = new StreamIdentityToken(next.StreamIdToken().ToArray());
                MemoryMessageData message = MemoryMessageData.Create(streamIdentityToken.Guid, streamIdentityToken.Namespace, new ArraySegment<byte>(next.Payload().ToArray()));
                this.current = new MemoryBatchContainer<TSerializer>(message, this.serializer);

                return true;
            }

            public void Refresh(StreamSequenceToken token)
            {
            }

            public void RecordDeliveryFailure()
            {
            }
        }

        /// <summary>
        /// The limit of the maximum number of items that can be added
        /// </summary>
        public int GetMaxAddCount() => 100;

        /// <summary>
        /// Add messages to the cache
        /// </summary>
        /// <param name="messages"></param>
        public void AddToCache(IList<IBatchContainer> messages)
        {
            DateTime utcNow = DateTime.UtcNow;
            List<CachedMessage> memoryMessages = messages
                .Cast<MemoryBatchContainer<TSerializer>>()
                .Select(container => container.MessageData)
                .Select(batch => this.QueueMessageToCachedMessage(batch, utcNow))
                .ToList();
            this.cache.Add(memoryMessages, DateTime.UtcNow);
        }

        /// <summary>
        /// Ask the cache if it has items that can be purged from the cache 
        /// (so that they can be subsequently released them the underlying queue).
        /// </summary>
        /// <param name="purgedItems"></param>
        public bool TryPurgeFromCache(out IList<IBatchContainer> purgedItems)
        {
            purgedItems = null;
            this.evictionStrategy.TryEvict(this.cache, DateTime.UtcNow);
            return false;
        }

        /// <summary>
        /// Acquire a stream message cursor.  This can be used to retrieve messages from the
        ///   cache starting at the location indicated by the provided token.
        /// </summary>
        /// <param name="streamIdentity"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        public IQueueCacheCursor GetCacheCursor(IStreamIdentity streamIdentity, StreamSequenceToken token)
            => new Cursor(this.cache, this.serializer, streamIdentity, token);

        /// <summary>
        /// Returns true if this cache is under pressure.
        /// </summary>
        public bool IsUnderPressure()
            => false;
    }
}
