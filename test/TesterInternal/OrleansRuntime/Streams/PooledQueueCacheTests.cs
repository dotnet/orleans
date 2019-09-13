
using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Providers.Abstractions;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;
using Xunit;

namespace UnitTests.OrleansRuntime.Streams
{
    public class PooledQueueCacheTests
    {
        private const int PooledBufferCount = 8;
        private const int PooledBufferSize = 1 << 10; // 1K
        private const int MessageSize = 1 << 7; // 128
        private const int MessagesPerBuffer = 6;
        private const string TestStreamNamespace = "blarg";
        
        private class TestQueueMessage
        {
            private static readonly byte[] FixedMessage = new byte[MessageSize];
            public Guid StreamGuid;
            public string StreamNamespace;
            public long SequenceNumber;
            public readonly byte[] Data = FixedMessage;
            public DateTime EnqueueTimeUtc = DateTime.UtcNow;
        }

        private class TestBatchContainer : IBatchContainer
        {
            public Guid StreamGuid { get; set; }
            public string StreamNamespace { get; set; }
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

            public bool ShouldDeliver(IStreamIdentity stream, object filterData, StreamFilterPredicate shouldReceiveFunc)
            {
                throw new NotImplementedException();
            }
        }


        private class TestCacheDataAdapter : ICacheDataAdapter
        {
            public IBatchContainer GetBatchContainer(in CachedMessage cachedMessage)
            {
                StreamIdentityToken streamIdentityToken = new StreamIdentityToken(cachedMessage.StreamIdToken().ToArray());
                return new TestBatchContainer
                {
                    StreamGuid = streamIdentityToken.Guid,
                    StreamNamespace = streamIdentityToken.Namespace,
                    SequenceToken = new EventSequenceTokenV2(cachedMessage.SequenceToken().ToArray()),
                    Data = cachedMessage.Payload().ToArray()
                };
            }
        }

        private class CachedMessageConverter
        {
            private readonly byte[] Empty = new byte[0];
            private readonly IObjectPool<FixedSizeBuffer> bufferPool;
            private readonly IFiFoEvictionStrategy<CachedMessage> evictionStrategy;
            private FixedSizeBuffer currentBuffer;


            public CachedMessageConverter(IObjectPool<FixedSizeBuffer> bufferPool, IFiFoEvictionStrategy<CachedMessage> evictionStrategy)
            {
                this.bufferPool = bufferPool;
                this.evictionStrategy = evictionStrategy;
            }

            public CachedMessage ToCachedMessage(TestQueueMessage queueMessage, DateTime dequeueTimeUtc)
            {
                StreamPosition streamPosition = this.GetStreamPosition(queueMessage);
                return CachedMessage.Create(
                    streamPosition.SequenceToken.SequenceToken,
                    StreamIdentityToken.Create(streamPosition.StreamIdentity),
                    Empty,
                    queueMessage.Data,
                    queueMessage.EnqueueTimeUtc,
                    dequeueTimeUtc,
                    this.GetSegment);
            }

            private StreamPosition GetStreamPosition(TestQueueMessage queueMessage)
            {
                IStreamIdentity streamIdentity = new StreamIdentity(queueMessage.StreamGuid, queueMessage.StreamNamespace);
                StreamSequenceToken sequenceToken = new EventSequenceTokenV2(queueMessage.SequenceNumber);
                return new StreamPosition(streamIdentity, sequenceToken);
            }

            private ArraySegment<byte> GetSegment(int size)
            {
                // get segment from current block
                ArraySegment<byte> segment;
                if (this.currentBuffer == null || !this.currentBuffer.TryGetSegment(size, out segment))
                {
                    // no block or block full, get new block and try again
                    currentBuffer = this.bufferPool.Allocate();
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
            var cache = new PooledQueueCache(null, null);
            var evictionStrategy = new ChronologicalEvictionStrategy(new TimePurgePredicate(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10)), null, null);
            var converter = new CachedMessageConverter(bufferPool, evictionStrategy);

            RunGoldenPath(cache, dataAdapter, converter, 111);
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
            var cache = new PooledQueueCache(null, null);
            var evictionStrategy = new ChronologicalEvictionStrategy(new TimePurgePredicate(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10)), null, null);
            var converter = new CachedMessageConverter(bufferPool, evictionStrategy);

            int startSequenceNuber = 222;
            startSequenceNuber = RunGoldenPath(cache, dataAdapter, converter, startSequenceNuber);
            RunGoldenPath(cache, dataAdapter, converter, startSequenceNuber);
        }

        private int RunGoldenPath(PooledQueueCache cache, ICacheDataAdapter dataAdapter, CachedMessageConverter converter, int startOfCache)
        {
            int sequenceNumber = startOfCache;
            IBatchContainer batch;

            IStreamIdentity stream1 = new StreamIdentity(Guid.NewGuid(), TestStreamNamespace);
            IStreamIdentity stream2 = new StreamIdentity(Guid.NewGuid(), TestStreamNamespace);

            // now add messages into cache newer than cursor
            // Adding enough to fill the pool
            List<TestQueueMessage> messages = Enumerable.Range(0, MessagesPerBuffer * PooledBufferCount)
                .Select(i => new TestQueueMessage
                {
                    StreamGuid = i % 2 == 0 ? stream1.Guid : stream2.Guid,
                    StreamNamespace = TestStreamNamespace,
                    SequenceNumber = sequenceNumber + i
                })
                .ToList();
            DateTime utcNow = DateTime.UtcNow;
            List<CachedMessage> cachedMessages = messages
                .Select(m => converter.ToCachedMessage(m, utcNow))
                .ToList();

            // Ensure order
            CachedMessage previous = cachedMessages.First();
            foreach(CachedMessage check in cachedMessages.Skip(1))
            {
                Assert.True(previous.Compare(check.SequenceToken()) < 0);
                previous = check;
            }

            cache.Add(cachedMessages, utcNow);
            sequenceNumber += MessagesPerBuffer * PooledBufferCount;

            // get cursor for stream1, walk all the events in the stream using the cursor
            object stream1Cursor = cache.GetCursor(StreamIdentityToken.Create(stream1), new EventSequenceTokenV2(startOfCache).SequenceToken);
            int stream1EventCount = 0;
            while (cache.TryGetNextMessage(stream1Cursor, out CachedMessage next))
            {
                Assert.NotNull(stream1Cursor);
                batch = dataAdapter.GetBatchContainer(next);
                Assert.NotNull(stream1Cursor);
                Assert.Equal(stream1.Guid, batch.StreamGuid);
                Assert.Equal(TestStreamNamespace, batch.StreamNamespace);
                Assert.NotNull(batch.SequenceToken);
                stream1EventCount++;
            }
            Assert.Equal((sequenceNumber - startOfCache) / 2, stream1EventCount);

            // get cursor for stream2, walk all the events in the stream using the cursor
            object stream2Cursor = cache.GetCursor(StreamIdentityToken.Create(stream2), new EventSequenceTokenV2(startOfCache).SequenceToken);
            int stream2EventCount = 0;
            while (cache.TryGetNextMessage(stream2Cursor, out CachedMessage next))
            {
                Assert.NotNull(stream2Cursor);
                batch = dataAdapter.GetBatchContainer(next);
                Assert.NotNull(batch);
                Assert.Equal(stream2.Guid, batch.StreamGuid);
                Assert.Equal(TestStreamNamespace, batch.StreamNamespace);
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
                    StreamGuid = i % 2 == 0 ? stream1.Guid : stream2.Guid,
                    StreamNamespace = TestStreamNamespace,
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
                while (cache.TryGetNextMessage(stream1Cursor, out CachedMessage next))
                {
                    Assert.NotNull(stream1Cursor);
                    batch = dataAdapter.GetBatchContainer(next);
                    Assert.NotNull(batch);
                    Assert.Equal(stream1.Guid, batch.StreamGuid);
                    Assert.Equal(TestStreamNamespace, batch.StreamNamespace);
                    Assert.NotNull(batch.SequenceToken);
                    stream1EventCount++;
                }
                Assert.Equal((sequenceNumber - startOfCache) / 2, stream1EventCount);

                // walk all the events in the stream using the cursor
                while (cache.TryGetNextMessage(stream2Cursor, out CachedMessage next))
                {
                    Assert.NotNull(stream2Cursor);
                    batch = dataAdapter.GetBatchContainer(next);
                    Assert.NotNull(batch);
                    Assert.Equal(stream2.Guid, batch.StreamGuid);
                    Assert.Equal(TestStreamNamespace, batch.StreamNamespace);
                    Assert.NotNull(batch.SequenceToken);
                    stream2EventCount++;
                }
                Assert.Equal((sequenceNumber - startOfCache) / 2, stream2EventCount);
            }
            return sequenceNumber;
        }
    }
}
