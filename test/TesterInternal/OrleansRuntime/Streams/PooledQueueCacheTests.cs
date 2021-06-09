
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost.Utils;
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

        private class TestBatchContainer : IBatchContainer
        {
            public StreamId StreamId { get; set; }
            public StreamSequenceToken SequenceToken { get; set; }
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
                        string errmsg = String.Format(CultureInfo.InvariantCulture,
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

            int idx;
            var seqNumber = 123;
            var stream = StreamId.Create(TestStreamNamespace, Guid.NewGuid());

            // First and last messages destined for stream, following messages
            // destined for other streams
            for (idx = 0; idx < 20; idx++)
            {
                var now = DateTime.UtcNow;
                var msg = new TestQueueMessage
                {
                    StreamId = (idx == 0) ? stream : StreamId.Create(TestStreamNamespace, Guid.NewGuid()),
                    SequenceNumber = seqNumber + idx,
                };
                cache.Add(new List<CachedMessage>() { converter.ToCachedMessage(msg, now) }, now);
            }

            var cursor = cache.GetCursor(stream, new EventSequenceTokenV2(seqNumber));

            // Remove first message
            cache.RemoveOldestMessage();

            // Enqueue a new message for stream
            {
                idx++;
                var now = DateTime.UtcNow;
                var msg = new TestQueueMessage
                {
                    StreamId = stream,
                    SequenceNumber = seqNumber + idx,
                };
                cache.Add(new List<CachedMessage>() { converter.ToCachedMessage(msg, now) }, now);
            }

            // Should throw since we missed the first message
            Assert.Throws<QueueCacheMissException>(() => cache.TryGetNextMessage(cursor, out _));
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
