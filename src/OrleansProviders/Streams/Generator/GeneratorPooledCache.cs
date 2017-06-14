
using System;
using System.Collections.Generic;
using System.Globalization;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;
using static System.String;

namespace Orleans.Providers.Streams.Generator
{
    /// <summary>
    /// Pooled cache for generator stream provider
    /// </summary>
    public class GeneratorPooledCache : IQueueCache
    {
        private readonly PooledQueueCache<GeneratedBatchContainer, CachedMessage> cache;
        private GeneratorPooledCacheEvictionStrategy evictionStrategy;
        /// <summary>
        /// Pooled cache for generator stream provider
        /// </summary>
        /// <param name="bufferPool"></param>
        /// <param name="logger"></param>
        /// <param name="serializationManager"></param>
        /// <param name="cacheMonitor"></param>
        /// <param name="monitorWriteInterval"></param>
        public GeneratorPooledCache(IObjectPool<FixedSizeBuffer> bufferPool, Logger logger, SerializationManager serializationManager, ICacheMonitor cacheMonitor, TimeSpan? monitorWriteInterval)
        {
            var dataAdapter = new CacheDataAdapter(bufferPool, serializationManager);
            cache = new PooledQueueCache<GeneratedBatchContainer, CachedMessage>(dataAdapter, CacheDataComparer.Instance, logger, cacheMonitor, monitorWriteInterval);
            TimePurgePredicate purgePredicate = new TimePurgePredicate(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
            this.evictionStrategy = new GeneratorPooledCacheEvictionStrategy(logger, purgePredicate, cacheMonitor, monitorWriteInterval) {PurgeObservable = cache};
            EvictionStrategyCommonUtils.WireUpEvictionStrategy(cache, dataAdapter, this.evictionStrategy);
        }

        // For fast GC this struct should contain only value types.  I included streamNamespace because I'm lasy and this is test code, but it should not be in here.
        private struct CachedMessage
        {
            public Guid StreamGuid;
            public string StreamNamespace;
            public long SequenceNumber;
            public DateTime EnqueueTimeUtc;
            public DateTime DequeueTimeUtc;
            public ArraySegment<byte> Payload;
        }

        private class CacheDataComparer : ICacheDataComparer<CachedMessage>
        {
            public static readonly ICacheDataComparer<CachedMessage> Instance = new CacheDataComparer(); 

            public int Compare(CachedMessage cachedMessage, StreamSequenceToken token)
            {
                var realToken = (EventSequenceTokenV2)token;
                return cachedMessage.SequenceNumber != realToken.SequenceNumber
                    ? (int)(cachedMessage.SequenceNumber - realToken.SequenceNumber)
                    : 0 - realToken.EventIndex;
            }

            public bool Equals(CachedMessage cachedMessage, IStreamIdentity streamIdentity)
            {
                var results = cachedMessage.StreamGuid.CompareTo(streamIdentity.Guid);
                return results == 0 && string.Compare(cachedMessage.StreamNamespace, streamIdentity.Namespace, StringComparison.Ordinal)==0;
            }
        }

        private class GeneratorPooledCacheEvictionStrategy : ChronologicalEvictionStrategy<CachedMessage>
        {
            public GeneratorPooledCacheEvictionStrategy(Logger logger, TimePurgePredicate purgePredicate, ICacheMonitor cacheMonitor, TimeSpan? monitorWriteInterval)
                : base(logger, purgePredicate, cacheMonitor, monitorWriteInterval)
            {            
            }

            protected override object GetBlockId(CachedMessage? cachedMessage)
            {
                return cachedMessage?.Payload.Array;
            }

            protected override DateTime GetDequeueTimeUtc(ref CachedMessage cachedMessage)
            {
                return cachedMessage.DequeueTimeUtc;
            }

            protected override DateTime GetEnqueueTimeUtc(ref CachedMessage cachedMessage)
            {
                return cachedMessage.EnqueueTimeUtc;
            }
        }

        private class CacheDataAdapter : ICacheDataAdapter<GeneratedBatchContainer, CachedMessage>
        {
            private readonly IObjectPool<FixedSizeBuffer> bufferPool;
            private readonly SerializationManager serializationManager;
            private FixedSizeBuffer currentBuffer;

            public Action<FixedSizeBuffer> OnBlockAllocated { private get; set; }

            public CacheDataAdapter(IObjectPool<FixedSizeBuffer> bufferPool, SerializationManager serializationManager)
            {
                if (bufferPool == null)
                {
                    throw new ArgumentNullException(nameof(bufferPool));
                }
                this.bufferPool = bufferPool;
                this.serializationManager = serializationManager;
            }

            public DateTime? GetMessageEnqueueTimeUtc(ref CachedMessage message)
            {
                return message.EnqueueTimeUtc;
            }

            public DateTime? GetMessageDequeueTimeUtc(ref CachedMessage message)
            {
                return message.DequeueTimeUtc;
            }

            public StreamPosition QueueMessageToCachedMessage(ref CachedMessage cachedMessage, GeneratedBatchContainer queueMessage, DateTime dequeueTimeUtc)
            {
                StreamPosition setreamPosition = GetStreamPosition(queueMessage);
                cachedMessage.StreamGuid = setreamPosition.StreamIdentity.Guid;
                cachedMessage.StreamNamespace = setreamPosition.StreamIdentity.Namespace;
                cachedMessage.SequenceNumber = queueMessage.RealToken.SequenceNumber;
                cachedMessage.EnqueueTimeUtc = queueMessage.EnqueueTimeUtc;
                cachedMessage.DequeueTimeUtc = dequeueTimeUtc;
                cachedMessage.Payload = SerializeMessageIntoPooledSegment(queueMessage);
                return setreamPosition;
            }

            // Placed object message payload into a segment from a buffer pool.  When this get's too big, older blocks will be purged
            private ArraySegment<byte> SerializeMessageIntoPooledSegment(GeneratedBatchContainer queueMessage)
            {
                // serialize payload
                byte[] serializedPayload = this.serializationManager.SerializeToByteArray(queueMessage.Payload);
                int size = serializedPayload.Length;

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
                        string errmsg = Format(CultureInfo.InvariantCulture,
                            "Message size is to big. MessageSize: {0}", size);
                        throw new ArgumentOutOfRangeException(nameof(queueMessage), errmsg);
                    }
                }
                Buffer.BlockCopy(serializedPayload, 0, segment.Array, segment.Offset, size);
                return segment;
            }

            public IBatchContainer GetBatchContainer(ref CachedMessage cachedMessage)
            {
                //Deserialize payload
                var stream = new BinaryTokenStreamReader(cachedMessage.Payload);
                object payloadObject = this.serializationManager.Deserialize(stream);
                return new GeneratedBatchContainer(cachedMessage.StreamGuid, cachedMessage.StreamNamespace,
                    payloadObject, new EventSequenceTokenV2(cachedMessage.SequenceNumber));
            }

            public StreamSequenceToken GetSequenceToken(ref CachedMessage cachedMessage)
            {
                return new EventSequenceTokenV2(cachedMessage.SequenceNumber);
            }

            public StreamPosition GetStreamPosition(GeneratedBatchContainer queueMessage)
            {
                return new StreamPosition(new StreamIdentity(queueMessage.StreamGuid, queueMessage.StreamNamespace), queueMessage.RealToken);
            }
        }

        private class Cursor : IQueueCacheCursor
        {
            private readonly PooledQueueCache<GeneratedBatchContainer, CachedMessage> cache;
            private readonly object cursor;
            private IBatchContainer current;

            public Cursor(PooledQueueCache<GeneratedBatchContainer, CachedMessage> cache, IStreamIdentity streamIdentity, StreamSequenceToken token)
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
        public int GetMaxAddCount() { return 100; }

        /// <summary>
        /// Add messages to the cache
        /// </summary>
        /// <param name="messages"></param>
        public void AddToCache(IList<IBatchContainer> messages)
        {
            DateTime dequeueTimeUtc = DateTime.UtcNow;
            foreach (IBatchContainer container in messages)
            {
                cache.Add(container as GeneratedBatchContainer, dequeueTimeUtc);
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
        /// Acquire a stream message cursor.  This can be used to retreave messages from the
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
