using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using Xunit;

namespace UnitTests.OrleansRuntime.Streams
{
    public class PooledQueueCacheTests
    {
        private const int PooledBufferCount = 8;
        private const int PooledBufferSize = 1 << 10; // 1K
        private const int MessageSize = 1 << 7; // 128
        private const int MessagesPerBuffer = 8;
        private const string TestStreamNamespace = "blarg";
        
        private class TestQueueMessage
        {
            private static readonly byte[] FixedMessage = new byte[MessageSize];
            public StreamId StreamId;
            public long SequenceNumber;
            public readonly byte[] Data = FixedMessage;
            public DateTime EnqueueTimeUtc = DateTime.UtcNow;
        }

        [GenerateSerializer]
        public class TestBatchContainer : IBatchContainer
        {
            [Id(0)]
            public StreamId StreamId { get; set; }

            [Id(1)]
            public StreamSequenceToken SequenceToken { get; set; }

            [Id(2)]
            public byte[] Data { get; set; }

            public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
            {
                throw new NotImplementedException();
            }

            public bool ImportRequestContext()
            {
                throw new NotImplementedException();
            }
        }


        private class TestCacheDataAdapter : ICacheDataAdapter
        {
            public IBatchContainer GetBatchContainer(ref CachedMessage cachedMessage)
            {
                //Deserialize payload
                int readOffset = 0;
                ArraySegment<byte> payload = SegmentBuilder.ReadNextBytes(cachedMessage.Segment, ref readOffset);

                return new TestBatchContainer
                {
                    StreamId =  cachedMessage.StreamId,
                    SequenceToken = GetSequenceToken(ref cachedMessage),
                    Data = payload.ToArray()
                };
            }

            public StreamSequenceToken GetSequenceToken(ref CachedMessage cachedMessage)
            {
                return new EventSequenceTokenV2(cachedMessage.SequenceNumber);
            }

        }

        private class CachedMessageConverter
        {
            private readonly IObjectPool<FixedSizeBuffer> bufferPool;
            private readonly IEvictionStrategy evictionStrategy;
            private FixedSizeBuffer currentBuffer;


            public CachedMessageConverter(IObjectPool<FixedSizeBuffer> bufferPool, IEvictionStrategy evictionStrategy)
            {
                this.bufferPool = bufferPool;
                this.evictionStrategy = evictionStrategy;
            }

            public CachedMessage ToCachedMessage(TestQueueMessage queueMessage, DateTime dequeueTimeUtc)
            {
                StreamPosition streamPosition = GetStreamPosition(queueMessage);
                return new CachedMessage
                {
                    StreamId = streamPosition.StreamId,
                    SequenceNumber = queueMessage.SequenceNumber,
                    EnqueueTimeUtc = queueMessage.EnqueueTimeUtc,
                    DequeueTimeUtc = dequeueTimeUtc,
                    Segment = SerializeMessageIntoPooledSegment(queueMessage),
                };
            }

            private StreamPosition GetStreamPosition(TestQueueMessage queueMessage)
            {
                StreamSequenceToken sequenceToken = new EventSequenceTokenV2(queueMessage.SequenceNumber);
                return new StreamPosition(queueMessage.StreamId, sequenceToken);
            }

            private ArraySegment<byte> SerializeMessageIntoPooledSegment(TestQueueMessage queueMessage)
            {
                // serialize payload
                int size = SegmentBuilder.CalculateAppendSize(queueMessage.Data);

                // get segment from current block
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
                        string errmsg = string.Format(CultureInfo.InvariantCulture,
                            "Message size is too big. MessageSize: {0}", size);
                        throw new ArgumentOutOfRangeException(nameof(queueMessage), errmsg);
                    }
                }
                // encode namespace, offset, partitionkey, properties and payload into segment
                int writeOffset = 0;
                SegmentBuilder.Append(segment, ref writeOffset, queueMessage.Data);
                return segment;
            }
        }

        /// <summary>
        /// Fill the cache with 2 streams.
        /// Get valid cursor to start of each stream.
        /// Walk each cursor until there is no more data on each stream.
        /// Alternate adding messages to cache and walking cursors.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void GoldenPathTest()
        {
            var bufferPool = new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(PooledBufferSize));
            var dataAdapter = new TestCacheDataAdapter();
            var cache = new PooledQueueCache(dataAdapter, NullLogger.Instance, null, null);
            var evictionStrategy = new ChronologicalEvictionStrategy(NullLogger.Instance, new TimePurgePredicate(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10)), null, null);
            evictionStrategy.PurgeObservable = cache;
            var converter = new CachedMessageConverter(bufferPool, evictionStrategy);

            RunGoldenPath(cache, converter, 111);
        }

        /// <summary>
        /// Run normal golden path test, then purge the cache, and then run another golden path test.  
        /// Goal is to make sure cache cleans up correctly when all data is purged.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void CacheDrainTest()
        {
            var bufferPool = new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(PooledBufferSize));
            var dataAdapter = new TestCacheDataAdapter();
            var cache = new PooledQueueCache(dataAdapter, NullLogger.Instance, null, null);
            var evictionStrategy = new ChronologicalEvictionStrategy(NullLogger.Instance, new TimePurgePredicate(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10)), null, null);
            evictionStrategy.PurgeObservable = cache;
            var converter = new CachedMessageConverter(bufferPool, evictionStrategy);

            int startSequenceNuber = 222;
            startSequenceNuber = RunGoldenPath(cache, converter, startSequenceNuber);
            RunGoldenPath(cache, converter, startSequenceNuber);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void AvoidCacheMissNotEmptyCache()
        {
            AvoidCacheMiss(false);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void AvoidCacheMissEmptyCache()
        {
            AvoidCacheMiss(true);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void AvoidCacheMissMultipleStreamsActive()
        {
            var bufferPool = new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(PooledBufferSize));
            var dataAdapter = new TestCacheDataAdapter();
            var cache = new PooledQueueCache(dataAdapter, NullLogger.Instance, null, null, TimeSpan.FromSeconds(30));
            var evictionStrategy = new ChronologicalEvictionStrategy(NullLogger.Instance, new TimePurgePredicate(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)), null, null);
            evictionStrategy.PurgeObservable = cache;
            var converter = new CachedMessageConverter(bufferPool, evictionStrategy);

            var seqNumber = 123;
            var streamKey = Guid.NewGuid();
            var stream = StreamId.Create(TestStreamNamespace, streamKey);

            // Enqueue a message for our stream
            var firstSequenceNumber = EnqueueMessage(streamKey);

            // Enqueue a few other messages for other streams
            EnqueueMessage(Guid.NewGuid());
            EnqueueMessage(Guid.NewGuid());

            // Consume the first event and see that the cursor has moved to last seen event (not matching our streamIdentity)
            var cursor = cache.GetCursor(stream, new EventSequenceTokenV2(firstSequenceNumber));
            Assert.True(cache.TryGetNextMessage(cursor, out var firstContainer));
            Assert.False(cache.TryGetNextMessage(cursor, out _));

            // Remove multiple events, including the one that the cursor is currently pointing to
            cache.RemoveOldestMessage();
            cache.RemoveOldestMessage();
            cache.RemoveOldestMessage();

            // Enqueue another message for stream
            var lastSequenceNumber = EnqueueMessage(streamKey);

            // Should be able to consume the event just pushed
            Assert.True(cache.TryGetNextMessage(cursor, out var lastContainer));
            Assert.Equal(stream, lastContainer.StreamId);
            Assert.Equal(lastSequenceNumber, lastContainer.SequenceToken.SequenceNumber);

            long EnqueueMessage(Guid streamId)
            {
                var now = DateTime.UtcNow;
                var msg = new TestQueueMessage
                {
                    StreamId = StreamId.Create(TestStreamNamespace, streamId),
                    SequenceNumber = seqNumber,
                };
                cache.Add(new List<CachedMessage>() { converter.ToCachedMessage(msg, now) }, now);
                seqNumber++;
                return msg.SequenceNumber;
            }
        }

        private void AvoidCacheMiss(bool emptyCache)
        {
            var bufferPool = new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(PooledBufferSize));
            var dataAdapter = new TestCacheDataAdapter();
            var cache = new PooledQueueCache(dataAdapter, NullLogger.Instance, null, null, TimeSpan.FromSeconds(30));
            var evictionStrategy = new ChronologicalEvictionStrategy(NullLogger.Instance, new TimePurgePredicate(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)), null, null);
            evictionStrategy.PurgeObservable = cache;
            var converter = new CachedMessageConverter(bufferPool, evictionStrategy);

            var seqNumber = 123;
            var stream = StreamId.Create(TestStreamNamespace, Guid.NewGuid());

            // Enqueue a message for stream
            var firstSequenceNumber = EnqueueMessage(stream);

            // Consume first event
            var cursor = cache.GetCursor(stream, new EventSequenceTokenV2(firstSequenceNumber));
            Assert.True(cache.TryGetNextMessage(cursor, out var firstContainer));
            Assert.Equal(stream, firstContainer.StreamId);
            Assert.Equal(firstSequenceNumber, firstContainer.SequenceToken.SequenceNumber);

            // Remove first message, that was consumed
            cache.RemoveOldestMessage();

            if (!emptyCache)
            {
                // Enqueue something not related to the stream
                // so the cache isn't empty
                EnqueueMessage(StreamId.Create(TestStreamNamespace, Guid.NewGuid()));
                EnqueueMessage(StreamId.Create(TestStreamNamespace, Guid.NewGuid()));
                EnqueueMessage(StreamId.Create(TestStreamNamespace, Guid.NewGuid()));
                EnqueueMessage(StreamId.Create(TestStreamNamespace, Guid.NewGuid()));
                EnqueueMessage(StreamId.Create(TestStreamNamespace, Guid.NewGuid()));
                EnqueueMessage(StreamId.Create(TestStreamNamespace, Guid.NewGuid()));
            }

            // Enqueue another message for stream
            var lastSequenceNumber = EnqueueMessage(stream);

            // Should be able to consume the event just pushed
            Assert.True(cache.TryGetNextMessage(cursor, out var lastContainer));
            Assert.Equal(stream, lastContainer.StreamId);
            Assert.Equal(lastSequenceNumber, lastContainer.SequenceToken.SequenceNumber);

            long EnqueueMessage(StreamId streamId)
            {
                var now = DateTime.UtcNow;
                var msg = new TestQueueMessage
                {
                    StreamId = streamId,
                    SequenceNumber = seqNumber,
                };
                cache.Add(new List<CachedMessage>() { converter.ToCachedMessage(msg, now) }, now);
                seqNumber++;
                return msg.SequenceNumber;
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void SimpleCacheMiss()
        {
            var bufferPool = new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(PooledBufferSize));
            var dataAdapter = new TestCacheDataAdapter();
            var cache = new PooledQueueCache(dataAdapter, NullLogger.Instance, null, null, TimeSpan.FromSeconds(10));
            var evictionStrategy = new ChronologicalEvictionStrategy(NullLogger.Instance, new TimePurgePredicate(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)), null, null);
            evictionStrategy.PurgeObservable = cache;
            var converter = new CachedMessageConverter(bufferPool, evictionStrategy);

            var seqNumber = 123;
            var streamKey = Guid.NewGuid();
            var stream = StreamId.Create(TestStreamNamespace, streamKey);

            var cursor = cache.GetCursor(stream, new EventSequenceTokenV2(seqNumber));
            // Start by enqueuing a message for stream, followed bu another one destined for another one
            EnqueueMessage(streamKey);
            EnqueueMessage(Guid.NewGuid());
            // Consume the stream, should be fine
            Assert.True(cache.TryGetNextMessage(cursor, out _));
            Assert.False(cache.TryGetNextMessage(cursor, out _));

            // Enqueue a new batch
            // First and last messages destined for stream, following messages
            // destined for other streams
            EnqueueMessage(streamKey);
            for (var idx = 0; idx < 20; idx++)
            {
                EnqueueMessage(Guid.NewGuid());
            }

            // Remove first three messages from the cache
            cache.RemoveOldestMessage(); // Destined for stream, consumed
            cache.RemoveOldestMessage(); // Not destined for stream
            cache.RemoveOldestMessage(); // Destined for stream, not consumed

            // Enqueue a new message for stream
            EnqueueMessage(streamKey);

            // Should throw since we missed the second message destined for stream
            Assert.Throws<QueueCacheMissException>(() => cache.TryGetNextMessage(cursor, out _));

            long EnqueueMessage(Guid streamId)
            {
                var now = DateTime.UtcNow;
                var msg = new TestQueueMessage
                {
                    StreamId = StreamId.Create(TestStreamNamespace, streamId),
                    SequenceNumber = seqNumber,
                };
                cache.Add(new List<CachedMessage>() { converter.ToCachedMessage(msg, now) }, now);
                seqNumber++;
                return msg.SequenceNumber;
            }
        }

        private int RunGoldenPath(PooledQueueCache cache, CachedMessageConverter converter, int startOfCache)
        {
            int sequenceNumber = startOfCache;
            IBatchContainer batch;

            var stream1 = StreamId.Create(TestStreamNamespace, Guid.NewGuid());
            var stream2 = StreamId.Create(TestStreamNamespace, Guid.NewGuid());

            // now add messages into cache newer than cursor
            // Adding enough to fill the pool
            List<TestQueueMessage> messages = Enumerable.Range(0, MessagesPerBuffer * PooledBufferCount)
                .Select(i => new TestQueueMessage
                {
                    StreamId = i % 2 == 0 ? stream1 : stream2,
                    SequenceNumber = sequenceNumber + i
                })
                .ToList();
            DateTime utcNow = DateTime.UtcNow;
            List<CachedMessage> cachedMessages = messages
                .Select(m => converter.ToCachedMessage(m, utcNow))
                .ToList();
            cache.Add(cachedMessages, utcNow);
            sequenceNumber += MessagesPerBuffer * PooledBufferCount;

            // get cursor for stream1, walk all the events in the stream using the cursor
            object stream1Cursor = cache.GetCursor(stream1, new EventSequenceTokenV2(startOfCache));
            int stream1EventCount = 0;
            while (cache.TryGetNextMessage(stream1Cursor, out batch))
            {
                Assert.NotNull(stream1Cursor);
                Assert.NotNull(batch);
                Assert.Equal(stream1, batch.StreamId);
                Assert.NotNull(batch.SequenceToken);
                stream1EventCount++;
            }
            Assert.Equal((sequenceNumber - startOfCache) / 2, stream1EventCount);

            // get cursor for stream2, walk all the events in the stream using the cursor
            object stream2Cursor = cache.GetCursor(stream2, new EventSequenceTokenV2(startOfCache));
            int stream2EventCount = 0;
            while (cache.TryGetNextMessage(stream2Cursor, out batch))
            {
                Assert.NotNull(stream2Cursor);
                Assert.NotNull(batch);
                Assert.Equal(stream2, batch.StreamId);
                Assert.NotNull(batch.SequenceToken);
                stream2EventCount++;
            }
            Assert.Equal((sequenceNumber - startOfCache) / 2, stream2EventCount);

            // Add a blocks worth of events to the cache, then walk each cursor.  Do this enough times to fill the cache twice.
            for (int j = 0; j < PooledBufferCount*2; j++)
            {
                List<TestQueueMessage> moreMessages = Enumerable.Range(0, MessagesPerBuffer)
                .Select(i => new TestQueueMessage
                {
                    StreamId = i % 2 == 0 ? stream1 : stream2,
                    SequenceNumber = sequenceNumber + i
                })
                .ToList();
                utcNow = DateTime.UtcNow;
                List<CachedMessage> moreCachedMessages = moreMessages
                    .Select(m => converter.ToCachedMessage(m, utcNow))
                    .ToList();
                cache.Add(moreCachedMessages, utcNow);
                sequenceNumber += MessagesPerBuffer;

                // walk all the events in the stream using the cursor
                while (cache.TryGetNextMessage(stream1Cursor, out batch))
                {
                    Assert.NotNull(stream1Cursor);
                    Assert.NotNull(batch);
                    Assert.Equal(stream1, batch.StreamId);
                    Assert.NotNull(batch.SequenceToken);
                    stream1EventCount++;
                }
                Assert.Equal((sequenceNumber - startOfCache) / 2, stream1EventCount);

                // walk all the events in the stream using the cursor
                while (cache.TryGetNextMessage(stream2Cursor, out batch))
                {
                    Assert.NotNull(stream2Cursor);
                    Assert.NotNull(batch);
                    Assert.Equal(stream2, batch.StreamId);
                    Assert.NotNull(batch.SequenceToken);
                    stream2EventCount++;
                }
                Assert.Equal((sequenceNumber - startOfCache) / 2, stream2EventCount);
            }
            return sequenceNumber;
        }
    }
}
