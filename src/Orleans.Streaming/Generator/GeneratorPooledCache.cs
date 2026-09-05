
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
    /// Pooled cache for generator stream provider.
    /// </summary>
    public class GeneratorPooledCache : IQueueCache, ICacheDataAdapter
    {
        private readonly IObjectPool<FixedSizeBuffer> bufferPool;
        private readonly Serialization.Serializer serializer;
        private readonly IEvictionStrategy evictionStrategy;
        private readonly PooledQueueCache cache;

        private FixedSizeBuffer? currentBuffer;

        /// <summary>
        /// Pooled cache for generator stream provider.
        /// </summary>
        /// <param name="bufferPool">The buffer pool.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="serializer">The serializer.</param>
        /// <param name="cacheMonitor">The cache monitor.</param>
        /// <param name="monitorWriteInterval">The monitor write interval. Only triggered for active caches</param>
        public GeneratorPooledCache(IObjectPool<FixedSizeBuffer> bufferPool, ILogger logger, Serialization.Serializer serializer, ICacheMonitor? cacheMonitor, TimeSpan? monitorWriteInterval)
        {
            this.bufferPool = bufferPool;
            this.serializer = serializer;
            cache = new PooledQueueCache(this, logger, cacheMonitor, monitorWriteInterval);
            TimePurgePredicate purgePredicate = new TimePurgePredicate(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
            this.evictionStrategy = new ChronologicalEvictionStrategy(logger, purgePredicate, cacheMonitor, monitorWriteInterval) { PurgeObservable = cache };
        }

        /// <inheritdoc />
        public IBatchContainer GetBatchContainer(ref CachedMessage cachedMessage)
        {
            //Deserialize payload
            int readOffset = 0;
            ArraySegment<byte> payload = SegmentBuilder.ReadNextBytes(cachedMessage.Segment, ref readOffset);
            // Generated batches always serialize a non-null payload.
            object payloadObject = this.serializer.Deserialize<object>(payload)!;
            return new GeneratedBatchContainer(cachedMessage.StreamId,
                payloadObject, new EventSequenceTokenV2(cachedMessage.SequenceNumber));
        }

        /// <inheritdoc />
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
                var newBuffer = bufferPool.Allocate();
                // if this fails with a clean block, then requested size is too big; return the
                // unused block to the pool and fail rather than leaking a block that is never committed.
                if (!newBuffer.TryGetSegment(size, out segment))
                {
                    newBuffer.Dispose();
                    string errmsg = $"Message size is too big. MessageSize: {size}";
                    throw new ArgumentOutOfRangeException(nameof(queueMessage), errmsg);
                }
                currentBuffer = newBuffer;
                //call EvictionStrategy's OnBlockAllocated method
                this.evictionStrategy.OnBlockAllocated(currentBuffer);
            }

            // encode namespace, offset, partitionkey, properties and payload into segment
            int writeOffset = 0;
            SegmentBuilder.Append(segment, ref writeOffset, serializedPayload);

            return segment;
        }

        private static StreamPosition GetStreamPosition(GeneratedBatchContainer queueMessage)
        {
            return new StreamPosition(queueMessage.StreamId, queueMessage.RealToken);
        }

        private class Cursor : IQueueCacheCursor
        {
            private readonly PooledQueueCache cache;
            private readonly object cursor;
            private IBatchContainer? current;

            public Cursor(PooledQueueCache cache, StreamId streamId, StreamSequenceToken? token)
            {
                this.cache = cache;
                cursor = GetCursorOrThrow(cache, streamId, token);
            }

            public Cursor(PooledQueueCache cache, object cursor)
            {
                this.cache = cache;
                this.cursor = cursor;
            }

            public void Dispose()
            {
            }

            public IBatchContainer? GetCurrent(out Exception? exception)
            {
                exception = null;
                return current;
            }

            [Obsolete("Use MoveNextWithResult instead.")]
            public bool MoveNext()
            {
                var result = cache.TryGetNextMessageWithResult(cursor, out var next);
                if (result.CacheMiss is { } cacheMiss)
                {
                    throw cacheMiss.ToException();
                }

                current = result.Kind == QueueCacheCursorMoveResultKind.Success ? next : null;
                return result.Kind switch
                {
                    QueueCacheCursorMoveResultKind.Success => true,
                    QueueCacheCursorMoveResultKind.NoData => false,
                    _ => throw new InvalidOperationException("The cursor move result is not initialized."),
                };
            }

            public QueueCacheCursorMoveResult MoveNextWithResult()
            {
                var result = cache.TryGetNextMessageWithResult(cursor, out var next);
                current = result.Kind == QueueCacheCursorMoveResultKind.Success ? next : null;
                return result;
            }

            public void Refresh(StreamSequenceToken token)
            {
                cache.Refresh(cursor, token);
            }

            public void RecordDeliveryFailure()
            {
            }

            private static object GetCursorOrThrow(
                PooledQueueCache cache,
                StreamId streamId,
                StreamSequenceToken? token)
            {
                var result = cache.TryGetCursor(streamId, token);
                return result.Kind switch
                {
                    QueueCacheCursorResultKind.Success => result.Cursor!,
                    QueueCacheCursorResultKind.CacheMiss => throw result.CacheMiss!.Value.ToException(),
                    _ => throw new InvalidOperationException($"Unexpected cursor result: {result.Kind}."),
                };
            }
        }

        /// <inheritdoc />
        public int GetMaxAddCount() { return 100; }

        /// <inheritdoc />
        public void AddToCache(IList<IBatchContainer> messages)
        {
            DateTime utcNow = DateTime.UtcNow;
            List<CachedMessage> generatedMessages = messages
                .Cast<GeneratedBatchContainer>()
                .Select(batch => QueueMessageToCachedMessage(batch, utcNow))
                .ToList();
            cache.Add(generatedMessages, utcNow);
        }

        /// <inheritdoc />
        public bool TryPurgeFromCache(out IList<IBatchContainer> purgedItems)
        {
            purgedItems = null!; // Return value is always false, per [MaybeNullWhen(false)] on the interface.
            this.evictionStrategy.PerformPurge(DateTime.UtcNow);
            return false;
        }

        /// <inheritdoc />
        [Obsolete("Use IQueueCache.TryGetCacheCursor instead.")]
        public IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken? token)
        {
            return new Cursor(cache, streamId, token);
        }

        QueueCacheCursorResult<IQueueCacheCursor> IQueueCache.TryGetCacheCursor(
            StreamId streamId,
            StreamSequenceToken? token)
        {
            return WrapCursorResult(cache.TryGetCursor(streamId, token));
        }

        QueueCacheCursorResult<IQueueCacheCursor> IQueueCache.TryGetCacheCursorAtPosition(
            StreamId streamId,
            StreamSubscriptionStartPosition startPosition)
        {
            return WrapCursorResult(cache.TryGetCursorAtPosition(streamId, startPosition));
        }

        /// <inheritdoc />
        [Obsolete("Use IQueueCache.TryGetCacheCursorAtPosition instead.")]
        IQueueCacheCursor IQueueCache.GetCacheCursorAtPosition(
            StreamId streamId,
            StreamSubscriptionStartPosition startPosition)
        {
#pragma warning disable CS0618 // Translate the pooled cache result for legacy callers.
            return new Cursor(cache, cache.GetCursorAtPosition(streamId, startPosition));
#pragma warning restore CS0618
        }

        private QueueCacheCursorResult<IQueueCacheCursor> WrapCursorResult(QueueCacheCursorResult<object> result)
            => result.Kind switch
            {
                QueueCacheCursorResultKind.Success => QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(new Cursor(cache, result.Cursor!)),
                QueueCacheCursorResultKind.CacheMiss => QueueCacheCursorResult<IQueueCacheCursor>.FromCacheMiss(result.CacheMiss!.Value),
                QueueCacheCursorResultKind.NotSupported => QueueCacheCursorResult<IQueueCacheCursor>.NotSupported,
                _ => throw new InvalidOperationException("The cursor result is not initialized."),
            };

        /// <inheritdoc />
        public bool IsUnderPressure()
        {
            return false;
        }
    }
}
