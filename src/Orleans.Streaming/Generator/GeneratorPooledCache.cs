
using System;
using System.Collections.Generic;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;
using System.Linq;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Providers.Streams.Generator
{
    /// <summary>
    /// Pooled cache for generator stream provider
    /// </summary>
    public class GeneratorPooledCache : IQueueCache, ICacheDataAdapter
    {
        private readonly IObjectPool<FixedSizeBuffer> bufferPool;
        private readonly Serialization.Serializer serializer;
        private readonly IEvictionStrategy evictionStrategy;
        private readonly PooledQueueCache cache;

        private FixedSizeBuffer currentBuffer;

        /// <summary>
        /// Pooled cache for generator stream provider
        /// </summary>
        /// <param name="bufferPool"></param>
        /// <param name="logger"></param>
        /// <param name="serializer"></param>
        /// <param name="cacheMonitor"></param>
        /// <param name="monitorWriteInterval">monitor write interval.  Only triggered for active caches.</param>
        public GeneratorPooledCache(IObjectPool<FixedSizeBuffer> bufferPool, ILogger logger, Serialization.Serializer serializer, ICacheMonitor cacheMonitor, TimeSpan? monitorWriteInterval)
        {
            this.bufferPool = bufferPool;
            this.serializer = serializer;
            cache = new PooledQueueCache(this, logger, cacheMonitor, monitorWriteInterval);
            TimePurgePredicate purgePredicate = new TimePurgePredicate(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
            this.evictionStrategy = new ChronologicalEvictionStrategy(logger, purgePredicate, cacheMonitor, monitorWriteInterval) {PurgeObservable = cache};
        }

        public IBatchContainer GetBatchContainer(ref CachedMessage cachedMessage)
        {
            //Deserialize payload
            int readOffset = 0;
            ArraySegment<byte> payload = SegmentBuilder.ReadNextBytes(cachedMessage.Segment, ref readOffset);
            object payloadObject = this.serializer.Deserialize<object>(payload);
            return new GeneratedBatchContainer(cachedMessage.StreamId,
                payloadObject, new EventSequenceTokenV2(cachedMessage.SequenceNumber));
        }

        public StreamSequenceToken GetSequenceToken(ref CachedMessage cachedMessage)
        {
            return new EventSequenceTokenV2(cachedMessage.SequenceNumber);
        }

        private CachedMessage QueueMessageToCachedMessage(GeneratedBatchContainer queueMessage, DateTime dequeueTimeUtc)
        {
            StreamPosition streamPosition = GetStreamPosition(queueMessage);
            return new CachedMessage()
            {
                StreamId = streamPosition.StreamId,
                SequenceNumber = queueMessage.RealToken.SequenceNumber,
                EnqueueTimeUtc = queueMessage.EnqueueTimeUtc,
                DequeueTimeUtc = dequeueTimeUtc,
                Segment = SerializeMessageIntoPooledSegment(queueMessage)
            };
        }

        // Placed object message payload into a segment from a buffer pool.  When this get's too big, older blocks will be purged
        private ArraySegment<byte> SerializeMessageIntoPooledSegment(GeneratedBatchContainer queueMessage)
        {
            byte[] serializedPayload = this.serializer.SerializeToArray(queueMessage.Payload);

            // get size of namespace, offset, partitionkey, properties, and payload
            int size = SegmentBuilder.CalculateAppendSize(serializedPayload);

            // get segment
            ArraySegment<byte> segment;
            if (currentBuffer == null || !currentBuffer.TryGetSegment(size, out segment))
            {
                // no block or block full, get new block and try again
                currentBuffer = bufferPool.Allocate();
                //call EvictionStrategy's OnBlockAllocated method
                this.evictionStrategy.OnBlockAllocated(currentBuffer);
                // if this fails with clean block, then requested size is too big
                if (!currentBuffer.TryGetSegment(size, out segment))
                {
                    string errmsg = $"Message size is to big. MessageSize: {size}";
                    throw new ArgumentOutOfRangeException(nameof(queueMessage), errmsg);
                }
            }

            // encode namespace, offset, partitionkey, properties and payload into segment
            int writeOffset = 0;
            SegmentBuilder.Append(segment, ref writeOffset, serializedPayload);

            return segment;
        }

        private StreamPosition GetStreamPosition(GeneratedBatchContainer queueMessage)
        {
            return new StreamPosition(queueMessage.StreamId, queueMessage.RealToken);
        }

        private class Cursor : IQueueCacheCursor
        {
            private readonly PooledQueueCache cache;
            private readonly object cursor;
            private IBatchContainer current;

            public Cursor(PooledQueueCache cache, StreamId streamId, StreamSequenceToken token)
            {
                this.cache = cache;
                cursor = cache.GetCursor(streamId, token);
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
            DateTime utcNow = DateTime.UtcNow;
            List<CachedMessage> generatedMessages = messages
                .Cast<GeneratedBatchContainer>()
                .Select(batch => QueueMessageToCachedMessage(batch, utcNow))
                .ToList();
            cache.Add(generatedMessages, utcNow);
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
        /// <param name="streamId"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        public IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken token)
        {
            return new Cursor(cache, streamId, token);
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
