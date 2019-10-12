
using System;
using System.Collections.Generic;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Streams;
using System.Linq;
using Orleans.Providers.Abstractions;

namespace Orleans.Providers.Streams.Generator
{
    /// <summary>
    /// Pooled cache for generator stream provider
    /// </summary>
    public class GeneratorPooledCache : IQueueCache
    {
        private static readonly byte[] offsetToken = new byte[0];

        private readonly IObjectPool<FixedSizeBuffer> bufferPool;
        private readonly SerializationManager serializationManager;
        private readonly IFiFoEvictionStrategy<CachedMessage> evictionStrategy;
        private readonly PooledQueueCache cache;

        private FixedSizeBuffer currentBuffer;

        /// <summary>
        /// Pooled cache for generator stream provider
        /// </summary>
        /// <param name="bufferPool"></param>
        /// <param name="serializationManager"></param>
        /// <param name="cacheMonitor"></param>
        /// <param name="monitorWriteInterval">monitor write interval.  Only triggered for active caches.</param>
        public GeneratorPooledCache(IObjectPool<FixedSizeBuffer> bufferPool, SerializationManager serializationManager, ICacheMonitor cacheMonitor, TimeSpan? monitorWriteInterval)
        {
            this.bufferPool = bufferPool;
            this.serializationManager = serializationManager;
            cache = new PooledQueueCache(cacheMonitor, monitorWriteInterval);
            TimePurgePredicate purgePredicate = new TimePurgePredicate(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
            this.evictionStrategy = new ChronologicalEvictionStrategy(purgePredicate, cacheMonitor, monitorWriteInterval);
        }

        private CachedMessage QueueMessageToCachedMessage(GeneratedBatchContainer queueMessage, DateTime dequeueTimeUtc)
        {
            StreamPosition streamPosition = this.GetStreamPosition(queueMessage);
            return CachedMessage.Create(
                streamPosition.SequenceToken.SequenceToken,
                StreamIdentityToken.Create(streamPosition.StreamIdentity),
                offsetToken,
                this.serializationManager.SerializeToByteArray(queueMessage.Payload),
                queueMessage.EnqueueTimeUtc,
                dequeueTimeUtc,
                this.GetSegment);
        }

        // Placed object message payload into a segment from a buffer pool.  When this get's too big, older blocks will be purged
        private ArraySegment<byte> GetSegment(int size)
        {
            // get segment
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
                    string errmsg = $"Message size is to big. MessageSize: {size}";
                    throw new ArgumentOutOfRangeException(nameof(size), errmsg);
                }
            }
            return segment;
        }

        private StreamPosition GetStreamPosition(GeneratedBatchContainer queueMessage)
            => new StreamPosition(new StreamIdentity(queueMessage.StreamGuid, queueMessage.StreamNamespace), queueMessage.RealToken);

        private class Cursor : IQueueCacheCursor
        {
            private readonly PooledQueueCache cache;
            private readonly SerializationManager serializationManager;
            private readonly object cursor;
            private IBatchContainer current;

            public Cursor(PooledQueueCache cache, SerializationManager serializationManager, IStreamIdentity streamIdentity, StreamSequenceToken token)
            {
                this.cache = cache;
                this.serializationManager = serializationManager;
                this.cursor = cache.GetCursor(StreamIdentityToken.Create(streamIdentity), token.SequenceToken);
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
                if (!this.cache.TryGetNextMessage(this.cursor, out CachedMessage next))
                {
                    return false;
                }

                var stream = new BinaryTokenStreamReader(next.Payload().ToArray());
                object payloadObject = this.serializationManager.Deserialize(stream);
                StreamIdentityToken streamIdentityToken = new StreamIdentityToken(next.StreamIdToken().ToArray());
                this.current = new GeneratedBatchContainer(streamIdentityToken.Guid, streamIdentityToken.Namespace,
                    payloadObject, new EventSequenceTokenV2(next.SequenceToken().ToArray()));

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
            List<CachedMessage> generatedMessages = messages
                .Cast<GeneratedBatchContainer>()
                .Select(batch => this.QueueMessageToCachedMessage(batch, utcNow))
                .ToList();
            this.cache.Add(generatedMessages, utcNow);
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
            => new Cursor(this.cache, this.serializationManager, streamIdentity, token);

        /// <summary>
        /// Returns true if this cache is under pressure.
        /// </summary>
        public bool IsUnderPressure() => false;
    }
}
