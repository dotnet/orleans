
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Orleans.Providers.Streams.Common;
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
            public Guid StreamGuid;
            public string StreamNamespace;
            public long SequenceNumber;
            public readonly byte[] Data = FixedMessage;
        }

        private struct TestCachedMessage
        {
            public Guid StreamGuid;
            public string StreamNamespace;
            public long SequenceNumber;
            public ArraySegment<byte> Payload;
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

        private class TestCacheDataComparer : ICacheDataComparer<TestCachedMessage>
        {
            public static readonly ICacheDataComparer<TestCachedMessage> Instance = new TestCacheDataComparer();

            public int Compare(TestCachedMessage cachedMessage, StreamSequenceToken token)
            {
                var realToken = (EventSequenceToken)token;
                return cachedMessage.SequenceNumber != realToken.SequenceNumber
                    ? (int)(cachedMessage.SequenceNumber - realToken.SequenceNumber)
                    : 0 - realToken.EventIndex;
            }

            public bool Equals(TestCachedMessage cachedMessage, IStreamIdentity streamIdentity)
            {
                int results = cachedMessage.StreamGuid.CompareTo(streamIdentity.Guid);
                return results == 0 && string.Compare(cachedMessage.StreamNamespace, streamIdentity.Namespace, StringComparison.Ordinal)==0;
            }
        }

        private class TestCacheDataAdapter : ICacheDataAdapter<TestQueueMessage, TestCachedMessage>
        {
            private readonly IObjectPool<FixedSizeBuffer> bufferPool;
            private FixedSizeBuffer currentBuffer;

            public Action<IDisposable> PurgeAction { private get; set; }

            public TestCacheDataAdapter(IObjectPool<FixedSizeBuffer> bufferPool)
            {
                if (bufferPool == null)
                {
                    throw new ArgumentNullException("bufferPool");
                }
                this.bufferPool = bufferPool;
            }

            public StreamPosition QueueMessageToCachedMessage(ref TestCachedMessage cachedMessage, TestQueueMessage queueMessage, DateTime dequeueTimeUtc)
            {
                StreamPosition streamPosition = GetStreamPosition(queueMessage);
                cachedMessage.StreamGuid = streamPosition.StreamIdentity.Guid;
                cachedMessage.StreamNamespace = streamPosition.StreamIdentity.Namespace;
                cachedMessage.SequenceNumber = queueMessage.SequenceNumber;
                cachedMessage.Payload = SerializeMessageIntoPooledSegment(queueMessage);
                return streamPosition;
            }

            // Placed object message payload into a segment from a buffer pool.  When this get's too big, older blocks will be purged
            private ArraySegment<byte> SerializeMessageIntoPooledSegment(TestQueueMessage queueMessage)
            {
                // get segment from current block
                ArraySegment<byte> segment;
                if (currentBuffer == null || !currentBuffer.TryGetSegment(queueMessage.Data.Length, out segment))
                {
                    // no block or block full, get new block and try again
                    currentBuffer = bufferPool.Allocate();
                    currentBuffer.SetPurgeAction(PurgeAction);
                    // if this fails with clean block, then requested size is too big
                    if (!currentBuffer.TryGetSegment(queueMessage.Data.Length, out segment))
                    {
                        string errmsg = String.Format(CultureInfo.InvariantCulture,
                            "Message size is to big. MessageSize: {0}", queueMessage.Data.Length);
                        throw new ArgumentOutOfRangeException("queueMessage", errmsg);
                    }
                }
                Buffer.BlockCopy(queueMessage.Data, 0, segment.Array, segment.Offset, queueMessage.Data.Length);
                return segment;
            }

            public IBatchContainer GetBatchContainer(ref TestCachedMessage cachedMessage)
            {
                return new TestBatchContainer
                {
                    StreamGuid =  cachedMessage.StreamGuid,
                    StreamNamespace = cachedMessage.StreamNamespace,
                    SequenceToken = GetSequenceToken(ref cachedMessage),
                    Data = cachedMessage.Payload.ToArray()
                };
            }

            public StreamSequenceToken GetSequenceToken(ref TestCachedMessage cachedMessage)
            {
                return new EventSequenceToken(cachedMessage.SequenceNumber);
            }

            public StreamPosition GetStreamPosition(TestQueueMessage queueMessage)
            {
                IStreamIdentity streamIdentity = new StreamIdentity(queueMessage.StreamGuid, queueMessage.StreamNamespace);
                StreamSequenceToken sequenceToken = new EventSequenceToken(queueMessage.SequenceNumber);
                return new StreamPosition(streamIdentity, sequenceToken);
            }

            public bool ShouldPurge(ref TestCachedMessage cachedMessage, ref TestCachedMessage newestCachedMessage, IDisposable purgeRequest, DateTime nowUtc)
            {
                var purgedResource = (FixedSizeBuffer)purgeRequest;
                // if we're purging our current buffer, don't use it any more
                if (currentBuffer != null && currentBuffer.Id == purgedResource.Id)
                {
                    currentBuffer = null;
                }
                return cachedMessage.Payload.Array == purgedResource.Id;
            }
        }

        private class TestBlockPool : FixedSizeObjectPool<FixedSizeBuffer>
        {
            // 10 buffers of 1k each
            public TestBlockPool()
                : base(PooledBufferCount, () => new FixedSizeBuffer(PooledBufferSize))
            {
            }

            public void PurgeAll()
            {
                while (usedObjects.Count != 0)
                {
                    usedObjects.Dequeue().SignalPurge();
                }
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
            var bufferPool = new TestBlockPool();
            var dataAdapter = new TestCacheDataAdapter(bufferPool);
            var cache = new PooledQueueCache<TestQueueMessage, TestCachedMessage>(dataAdapter, TestCacheDataComparer.Instance, NoOpTestLogger.Instance);
            dataAdapter.PurgeAction = cache.Purge;
            RunGoldenPath(cache, 111);
        }

        /// <summary>
        /// Run normal golden path test, then purge the cache, and then run another golden path test.  
        /// Goal is to make sure cache cleans up correctly when all data is purged.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void CacheDrainTest()
        {
            var bufferPool = new TestBlockPool();
            var dataAdapter = new TestCacheDataAdapter(bufferPool);
            var cache = new PooledQueueCache<TestQueueMessage, TestCachedMessage>(dataAdapter, TestCacheDataComparer.Instance, NoOpTestLogger.Instance);
            dataAdapter.PurgeAction = cache.Purge;
            int startSequenceNuber = 222;
            startSequenceNuber = RunGoldenPath(cache, startSequenceNuber);
            bufferPool.PurgeAll();
            RunGoldenPath(cache, startSequenceNuber);
        }

        private int RunGoldenPath(PooledQueueCache<TestQueueMessage, TestCachedMessage> cache, int startOfCache)
        {
            int sequenceNumber = startOfCache;
            IBatchContainer batch;

            IStreamIdentity stream1 = new StreamIdentity(Guid.NewGuid(), TestStreamNamespace);
            IStreamIdentity stream2 = new StreamIdentity(Guid.NewGuid(), TestStreamNamespace);

            // now add messages into cache newer than cursor
            // Adding enough to fill the pool
            for (int i = 0; i < MessagesPerBuffer * PooledBufferCount; i++)
            {
                cache.Add(new TestQueueMessage
                {
                    StreamGuid = i % 2 == 0 ? stream1.Guid : stream2.Guid,
                    StreamNamespace = TestStreamNamespace,
                    SequenceNumber = sequenceNumber++,
                }, DateTime.UtcNow);
            }

            // get cursor for stream1, walk all the events in the stream using the cursor
            object stream1Cursor = cache.GetCursor(stream1, new EventSequenceToken(startOfCache));
            int stream1EventCount = 0;
            while (cache.TryGetNextMessage(stream1Cursor, out batch))
            {
                Assert.NotNull(stream1Cursor);
                Assert.NotNull(batch);
                Assert.Equal(stream1.Guid, batch.StreamGuid);
                Assert.Equal(TestStreamNamespace, batch.StreamNamespace);
                Assert.NotNull(batch.SequenceToken);
                stream1EventCount++;
            }
            Assert.Equal((sequenceNumber - startOfCache) / 2, stream1EventCount);

            // get cursor for stream2, walk all the events in the stream using the cursor
            object stream2Cursor = cache.GetCursor(stream2, new EventSequenceToken(startOfCache));
            int stream2EventCount = 0;
            while (cache.TryGetNextMessage(stream2Cursor, out batch))
            {
                Assert.NotNull(stream2Cursor);
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
                for (int i = 0; i < MessagesPerBuffer; i++)
                {
                    cache.Add(new TestQueueMessage
                    {
                        StreamGuid = i % 2 == 0 ? stream1.Guid : stream2.Guid,
                        StreamNamespace = TestStreamNamespace,
                        SequenceNumber = sequenceNumber++,
                    }, DateTime.UtcNow);
                }

                // walk all the events in the stream using the cursor
                while (cache.TryGetNextMessage(stream1Cursor, out batch))
                {
                    Assert.NotNull(stream1Cursor);
                    Assert.NotNull(batch);
                    Assert.Equal(stream1.Guid, batch.StreamGuid);
                    Assert.Equal(TestStreamNamespace, batch.StreamNamespace);
                    Assert.NotNull(batch.SequenceToken);
                    stream1EventCount++;
                }
                Assert.Equal((sequenceNumber - startOfCache) / 2, stream1EventCount);

                // walk all the events in the stream using the cursor
                while (cache.TryGetNextMessage(stream2Cursor, out batch))
                {
                    Assert.NotNull(stream2Cursor);
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

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void QueueCacheMissTest()
        {
            var bufferPool = new TestBlockPool();
            var dataAdapter = new TestCacheDataAdapter(bufferPool);
            var cache = new PooledQueueCache<TestQueueMessage, TestCachedMessage>(dataAdapter, TestCacheDataComparer.Instance, NoOpTestLogger.Instance);
            dataAdapter.PurgeAction = cache.Purge;
            int sequenceNumber = 10;
            IBatchContainer batch;

            IStreamIdentity streamId = new StreamIdentity(Guid.NewGuid(), TestStreamNamespace);

            // No data in cache, cursors should not throw.
            object cursor = cache.GetCursor(streamId, new EventSequenceToken(sequenceNumber++));
            Assert.NotNull(cursor);

            // try to iterate, should throw
            bool gotNext = cache.TryGetNextMessage(cursor, out batch);
            Assert.NotNull(cursor);
            Assert.False(gotNext);

            // now add messages into cache newer than cursor
            // Adding enough to fill the pool
            for (int i = 0; i < MessagesPerBuffer * PooledBufferCount; i++)
            {
                cache.Add(new TestQueueMessage
                {
                    StreamGuid = streamId.Guid,
                    StreamNamespace = TestStreamNamespace,
                    SequenceNumber = sequenceNumber++,
                }, DateTime.UtcNow);
            }

            // now that there is data, and the cursor should point to data older than in the cache, using cursor should throw
            Exception ex = null;
            try
            {
                cache.TryGetNextMessage(cursor, out batch);
            }
            catch (QueueCacheMissException cacheMissException)
            {
                ex = cacheMissException;
            }
            Assert.NotNull(ex);

            // Try getting new cursor into cache from data before the cache.  Should throw
            ex = null;
            try
            {
                cache.GetCursor(streamId, new EventSequenceToken(10));
            }
            catch (QueueCacheMissException cacheMissException)
            {
                ex = cacheMissException;
            }
            Assert.NotNull(ex);

            // Get valid cursor into cache
            cursor = cache.GetCursor(streamId, new EventSequenceToken(13));
            // query once, to make sure cursor is good
            gotNext = cache.TryGetNextMessage(cursor, out batch);
            Assert.NotNull(cursor);
            Assert.True(gotNext);
            // Since pool should be full, adding one more message should trigger the cache to purge.  
            cache.Add(new TestQueueMessage
            {
                StreamGuid = streamId.Guid,
                StreamNamespace = TestStreamNamespace,
                SequenceNumber = sequenceNumber,
            }, DateTime.UtcNow);
            // After purge, use of cursor should throw.
            ex = null;
            try
            {
                cache.TryGetNextMessage(cursor, out batch);
            }
            catch (QueueCacheMissException cacheMissException)
            {
                ex = cacheMissException;
            }
            Assert.NotNull(ex);
        }
    }
}
