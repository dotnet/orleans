
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers
{
    /// <summary>
    /// Pooled cache for memory stream provider
    /// </summary>
    public class MemoryPooledCache<TSerializer> : IQueueCache
        where TSerializer : class, IMemoryMessageBodySerializer
    {
        private readonly PooledQueueCache<MemoryMessageData, MemoryMessageData> cache;
        private MemoryPooledCacheEvictionStrategy evictionStrategy;
        /// <summary>
        /// Pooled cache for memory stream provider
        /// </summary>
        /// <param name="bufferPool"></param>
        /// <param name="purgePredicate"></param>
        /// <param name="logger"></param>
        /// <param name="serializer"></param>
        /// <param name="cacheMonitor"></param>
        /// <param name="monitorWriteInterval"></param>
        public MemoryPooledCache(IObjectPool<FixedSizeBuffer> bufferPool, TimePurgePredicate purgePredicate, Logger logger, TSerializer serializer, ICacheMonitor cacheMonitor, TimeSpan? monitorWriteInterval)
        {
            var dataAdapter = new CacheDataAdapter(bufferPool, serializer);
            cache = new PooledQueueCache<MemoryMessageData, MemoryMessageData>(dataAdapter, CacheDataComparer.Instance, logger, cacheMonitor, monitorWriteInterval);
            this.evictionStrategy = new MemoryPooledCacheEvictionStrategy(logger, purgePredicate, cacheMonitor, monitorWriteInterval) {PurgeObservable = cache};
            EvictionStrategyCommonUtils.WireUpEvictionStrategy<MemoryMessageData, MemoryMessageData>(cache, dataAdapter, evictionStrategy);
        }

        private class CacheDataComparer : ICacheDataComparer<MemoryMessageData>
        {
            public static readonly ICacheDataComparer<MemoryMessageData> Instance = new CacheDataComparer();

            public int Compare(MemoryMessageData cachedMessage, StreamSequenceToken token)
            {
                var realToken = (EventSequenceToken)token;
                return cachedMessage.SequenceNumber != realToken.SequenceNumber
                    ? (int)(cachedMessage.SequenceNumber - realToken.SequenceNumber)
                    : 0 - realToken.EventIndex;
            }

            public bool Equals(MemoryMessageData cachedMessage, IStreamIdentity streamIdentity)
            {
                int results = cachedMessage.StreamGuid.CompareTo(streamIdentity.Guid);
                return results == 0 && cachedMessage.StreamNamespace == streamIdentity.Namespace;
            }
        }

        private class MemoryPooledCacheEvictionStrategy : ChronologicalEvictionStrategy<MemoryMessageData>
        {
            public MemoryPooledCacheEvictionStrategy(Logger logger, TimePurgePredicate purgePredicate, ICacheMonitor cacheMonitor, TimeSpan? monitorWriteInterval)
                : base(logger, purgePredicate, cacheMonitor, monitorWriteInterval)
            {
            }

            protected override object GetBlockId(MemoryMessageData? cachedMessage)
            {
                return cachedMessage?.Payload.Array;
            }

            protected override DateTime GetDequeueTimeUtc(ref MemoryMessageData cachedMessage)
            {
                return cachedMessage.DequeueTimeUtc;
            }

            protected override DateTime GetEnqueueTimeUtc(ref MemoryMessageData cachedMessage)
            {
                return cachedMessage.EnqueueTimeUtc;
            }
        }

        private class CacheDataAdapter : ICacheDataAdapter<MemoryMessageData, MemoryMessageData>
        {
            private readonly IObjectPool<FixedSizeBuffer> bufferPool;
            private readonly TSerializer serializer;
            private FixedSizeBuffer currentBuffer;

            public Action<FixedSizeBuffer> OnBlockAllocated { private get; set; }

            public CacheDataAdapter(IObjectPool<FixedSizeBuffer> bufferPool, TSerializer serializer)
            {
                if (bufferPool == null)
                {
                    throw new ArgumentNullException(nameof(bufferPool));
                }
                this.bufferPool = bufferPool;
                this.serializer = serializer;
            }

            public DateTime? GetMessageEnqueueTimeUtc(ref MemoryMessageData message)
            {
                return message.EnqueueTimeUtc;
            }

            public DateTime? GetMessageDequeueTimeUtc(ref MemoryMessageData message)
            {
                return message.DequeueTimeUtc;
            }

            public StreamPosition QueueMessageToCachedMessage(ref MemoryMessageData cachedMessage,
                MemoryMessageData queueMessage, DateTime dequeueTimeUtc)
            {
                StreamPosition setreamPosition = GetStreamPosition(queueMessage);
                cachedMessage = queueMessage;
                cachedMessage.DequeueTimeUtc = dequeueTimeUtc;
                cachedMessage.Payload = SerializeMessageIntoPooledSegment(queueMessage);
                return setreamPosition;
            }

            // Placed object message payload into a segment from a buffer pool.  When this get's too big, older blocks will be purged
            private ArraySegment<byte> SerializeMessageIntoPooledSegment(MemoryMessageData queueMessage)
            {
                // serialize payload
                int size = queueMessage.Payload.Count;

                // get segment from current block
                ArraySegment<byte> segment;
                if (currentBuffer == null || !currentBuffer.TryGetSegment(size, out segment))
                {
                    // no block or block full, get new block and try again
                    currentBuffer = bufferPool.Allocate();
                    if (this.OnBlockAllocated == null)
                        throw new OrleansException("Eviction strategy's OnBlockAllocated is not set for current data adapter, this will affect cache purging");
                    //call EvictionStrategy's OnBlockAllocated method
                    this.OnBlockAllocated.Invoke(currentBuffer);
                    // if this fails with clean block, then requested size is too big
                    if (!currentBuffer.TryGetSegment(size, out segment))
                    {
                        string errmsg = String.Format(CultureInfo.InvariantCulture,
                            "Message size is too big. MessageSize: {0}", size);
                        throw new ArgumentOutOfRangeException(nameof(queueMessage), errmsg);
                    }
                }
                Buffer.BlockCopy(queueMessage.Payload.Array, queueMessage.Payload.Offset, segment.Array, segment.Offset, queueMessage.Payload.Count);
                return segment;
            }

            public IBatchContainer GetBatchContainer(ref MemoryMessageData cachedMessage)
            {
                MemoryMessageData messageData = cachedMessage;
                messageData.Payload = new ArraySegment<byte>(cachedMessage.Payload.ToArray());
                return new MemoryBatchContainer<TSerializer>(messageData, this.serializer);
            }

            public StreamSequenceToken GetSequenceToken(ref MemoryMessageData cachedMessage)
            {
                return new EventSequenceToken(cachedMessage.SequenceNumber);
            }

            public StreamPosition GetStreamPosition(MemoryMessageData queueMessage)
            {
                return new StreamPosition(new StreamIdentity(queueMessage.StreamGuid, queueMessage.StreamNamespace),
                    new EventSequenceTokenV2(queueMessage.SequenceNumber));
            }
        }

        private class Cursor : IQueueCacheCursor
        {
            private readonly PooledQueueCache<MemoryMessageData, MemoryMessageData> cache;
            private readonly object cursor;
            private IBatchContainer current;

            public Cursor(PooledQueueCache<MemoryMessageData, MemoryMessageData> cache, IStreamIdentity streamIdentity,
                StreamSequenceToken token)
            {
                this.cache = cache;
                cursor = cache.GetCursor(streamIdentity, token);
            }

            public void Dispose()
            {
            }

            public IBatchContainer GetCurrent(out Exception exception)
            {
                exception = null;
                return current;
            }

            public bool MoveNext()
            {
                IBatchContainer next;
                if (!cache.TryGetNextMessage(cursor, out next))
                {
                    return false;
                }

                current = next;
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
        public int GetMaxAddCount()
        {
            return 100;
        }

        /// <summary>
        /// Add messages to the cache
        /// </summary>
        /// <param name="messages"></param>
        public void AddToCache(IList<IBatchContainer> messages)
        {
            DateTime dequeueTimeUtc = DateTime.UtcNow;
            foreach (IBatchContainer container in messages)
            {
                MemoryBatchContainer<TSerializer> memoryBatchContainer = (MemoryBatchContainer<TSerializer>) container;
                cache.Add(memoryBatchContainer.MessageData, dequeueTimeUtc);
            }
        }

        /// <summary>
        /// Ask the cache if it has items that can be purged from the cache 
        /// (so that they can be subsequently released them the underlying queue).
        /// </summary>
        /// <param name="purgedItems"></param>
        public bool TryPurgeFromCache(out IList<IBatchContainer> purgedItems)
        {
            purgedItems = null;
            this.evictionStrategy.PerformPurge(DateTime.UtcNow);
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
        {
            return new Cursor(cache, streamIdentity, token);
        }

        /// <summary>
        /// Returns true if this cache is under pressure.
        /// </summary>
        public bool IsUnderPressure()
        {
            return false;
        }
    }
}
