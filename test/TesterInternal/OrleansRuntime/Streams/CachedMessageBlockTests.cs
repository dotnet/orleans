
using System;
using System.Collections.Generic;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;
using Xunit;

namespace UnitTests.OrleansRuntime.Streams
{
    public class CachedMessageBlockTests
    {
        private const int TestBlockSize = 100;
        private readonly Guid StreamGuid = Guid.NewGuid();

        private class TestQueueMessage
        {
            public Guid StreamGuid { get; set; }
            public EventSequenceToken SequenceToken { get; set; }
        }

        private struct TestCachedMessage
        {
            public Guid StreamGuid { get; set; }
            public long SequenceNumber { get; set; }
            public int EventIndex { get; set; }
        }

        private class TestBatchContainer : IBatchContainer
        {
            public Guid StreamGuid { get; set; }
            public string StreamNamespace => null;
            public StreamSequenceToken SequenceToken { get; set; }

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
                var myToken = new EventSequenceToken(cachedMessage.SequenceNumber, cachedMessage.EventIndex);
                return myToken.CompareTo(token);
            }

            public bool Equals(TestCachedMessage cachedMessage, IStreamIdentity streamIdentity)
            {
                return cachedMessage.StreamGuid.CompareTo(streamIdentity.Guid)==0;
            }
        }


        private class TestCacheDataAdapter : ICacheDataAdapter<TestQueueMessage, TestCachedMessage>
        {
            public Action<IDisposable> PurgeAction { private get; set; }

            public StreamPosition QueueMessageToCachedMessage(ref TestCachedMessage cachedMessage, TestQueueMessage queueMessage, DateTime dequeueTimeUtc)
            {
                StreamPosition streamPosition = GetStreamPosition(queueMessage);
                cachedMessage.StreamGuid = streamPosition.StreamIdentity.Guid;
                cachedMessage.SequenceNumber = queueMessage.SequenceToken.SequenceNumber;
                cachedMessage.EventIndex = queueMessage.SequenceToken.EventIndex;
                return streamPosition;
            }

            public IBatchContainer GetBatchContainer(ref TestCachedMessage cachedMessage)
            {
                return new TestBatchContainer()
                {
                    StreamGuid = cachedMessage.StreamGuid,
                    SequenceToken = new EventSequenceToken(cachedMessage.SequenceNumber, cachedMessage.EventIndex)
                };
            }

            public StreamSequenceToken GetSequenceToken(ref TestCachedMessage cachedMessage)
            {
                return new EventSequenceToken(cachedMessage.SequenceNumber, cachedMessage.EventIndex);
            }

            public StreamPosition GetStreamPosition(TestQueueMessage queueMessage)
            {
                IStreamIdentity streamIdentity = new StreamIdentity(queueMessage.StreamGuid, null);
                StreamSequenceToken sequenceToken = queueMessage.SequenceToken;
                return new StreamPosition(streamIdentity, sequenceToken);
            }

            public bool ShouldPurge(ref TestCachedMessage cachedMessage, ref TestCachedMessage newestCachedMessage, IDisposable purgeRequest, DateTime nowUtc)
            {
                throw new NotImplementedException();
            }
        }

        private class MyTestPooled : IObjectPool<CachedMessageBlock<TestCachedMessage>>
        {
            public CachedMessageBlock<TestCachedMessage> Allocate()
            {
                return new CachedMessageBlock<TestCachedMessage>(TestBlockSize){Pool = this};
            }

            public void Free(CachedMessageBlock<TestCachedMessage> resource)
            {
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void Add1Remove1Test()
        {
            IObjectPool<CachedMessageBlock<TestCachedMessage>> pool = new MyTestPooled();
            ICacheDataAdapter<TestQueueMessage, TestCachedMessage> dataAdapter = new TestCacheDataAdapter();
            CachedMessageBlock<TestCachedMessage> block = pool.Allocate();

            AddAndCheck(block, dataAdapter, 0, -1);
            RemoveAndCheck(block, 0, 0);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void Add2Remove1UntilFull()
        {
            IObjectPool<CachedMessageBlock<TestCachedMessage>> pool = new MyTestPooled();
            ICacheDataAdapter<TestQueueMessage, TestCachedMessage> dataAdapter = new TestCacheDataAdapter();
            CachedMessageBlock<TestCachedMessage> block = pool.Allocate();
            int first = 0;
            int last = -1;

            while (block.HasCapacity)
            {
                // add message to end of block
                AddAndCheck(block, dataAdapter, first, last);
                last++;
                if (!block.HasCapacity)
                {
                    continue;
                }
                // add message to end of block
                AddAndCheck(block, dataAdapter, first, last);
                last++;
                // removed message from start of block
                RemoveAndCheck(block, first, last);
                first++;
            }
            Assert.Equal(TestBlockSize / 2, block.OldestMessageIndex);
            Assert.Equal(TestBlockSize - 1, block.NewestMessageIndex);
            Assert.False(block.IsEmpty);
            Assert.False(block.HasCapacity);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void FirstMessageWithSequenceNumberTest()
        {
            IObjectPool<CachedMessageBlock<TestCachedMessage>> pool = new MyTestPooled();
            ICacheDataAdapter<TestQueueMessage, TestCachedMessage> dataAdapter = new TestCacheDataAdapter();
            CachedMessageBlock<TestCachedMessage> block = pool.Allocate();
            int last = -1;
            int sequenceNumber = 0;

            while (block.HasCapacity)
            {
                // add message to end of block
                AddAndCheck(block, dataAdapter, 0, last, sequenceNumber);
                last++;
                sequenceNumber += 2;
            }
            Assert.Equal(block.OldestMessageIndex, block.GetIndexOfFirstMessageLessThanOrEqualTo(new EventSequenceToken(0), TestCacheDataComparer.Instance));
            Assert.Equal(block.OldestMessageIndex, block.GetIndexOfFirstMessageLessThanOrEqualTo(new EventSequenceToken(1), TestCacheDataComparer.Instance));
            Assert.Equal(block.NewestMessageIndex, block.GetIndexOfFirstMessageLessThanOrEqualTo(new EventSequenceToken(sequenceNumber - 2), TestCacheDataComparer.Instance));
            Assert.Equal(block.NewestMessageIndex - 1, block.GetIndexOfFirstMessageLessThanOrEqualTo(new EventSequenceToken(sequenceNumber - 3), TestCacheDataComparer.Instance));
            Assert.Equal(50, block.GetIndexOfFirstMessageLessThanOrEqualTo(new EventSequenceToken(sequenceNumber / 2), TestCacheDataComparer.Instance));
            Assert.Equal(50, block.GetIndexOfFirstMessageLessThanOrEqualTo(new EventSequenceToken(sequenceNumber / 2 + 1), TestCacheDataComparer.Instance));
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void NextInStreamTest()
        {
            IObjectPool<CachedMessageBlock<TestCachedMessage>> pool = new MyTestPooled();
            ICacheDataAdapter<TestQueueMessage, TestCachedMessage> dataAdapter = new TestCacheDataAdapter();
            CachedMessageBlock<TestCachedMessage> block = pool.Allocate();
            int last = 0;
            int sequenceNumber = 0;
            // define 2 streams
            var streams = new[] { new StreamIdentity(Guid.NewGuid(), null), new StreamIdentity(Guid.NewGuid(), null) };

            // add both streams interleaved, until lock is full
            while (block.HasCapacity)
            {
                var stream = streams[last%2];
                var message = new TestQueueMessage
                {
                    StreamGuid = stream.Guid,
                    SequenceToken = new EventSequenceToken(sequenceNumber)
                };

                // add message to end of block
                AddAndCheck(block, dataAdapter, message, 0, last - 1);
                last++;
                sequenceNumber += 2;
            }

            // get index of first stream
            int streamIndex;
            Assert.True(block.TryFindFirstMessage(streams[0], TestCacheDataComparer.Instance, out streamIndex));
            Assert.Equal(0, streamIndex);
            Assert.Equal(0, (block.GetSequenceToken(streamIndex, dataAdapter) as EventSequenceToken).SequenceNumber);

            // find stream1 messages
            int iteration = 1;
            while (block.TryFindNextMessage(streamIndex + 1, streams[0], TestCacheDataComparer.Instance, out streamIndex))
            {
                Assert.Equal(iteration * 2, streamIndex);
                Assert.Equal(iteration * 4, (block.GetSequenceToken(streamIndex, dataAdapter) as EventSequenceToken).SequenceNumber);
                iteration++;
            }
            Assert.Equal(iteration, TestBlockSize / 2);

            // get index of first stream
            Assert.True(block.TryFindFirstMessage(streams[1], TestCacheDataComparer.Instance, out streamIndex));
            Assert.Equal(1, streamIndex);
            Assert.Equal(2, (block.GetSequenceToken(streamIndex, dataAdapter) as EventSequenceToken).SequenceNumber);

            // find stream1 messages
            iteration = 1;
            while (block.TryFindNextMessage(streamIndex + 1, streams[1], TestCacheDataComparer.Instance, out streamIndex))
            {
                Assert.Equal(iteration * 2 + 1, streamIndex);
                Assert.Equal(iteration * 4 + 2, (block.GetSequenceToken(streamIndex, dataAdapter) as EventSequenceToken).SequenceNumber);
                iteration++;
            }
            Assert.Equal(iteration, TestBlockSize / 2);
        }

        private void AddAndCheck(CachedMessageBlock<TestCachedMessage> block, ICacheDataAdapter<TestQueueMessage, TestCachedMessage> dataAdapter, int first, int last, int sequenceNumber = 1)
        {
            var message = new TestQueueMessage
            {
                StreamGuid = StreamGuid,
                SequenceToken = new EventSequenceToken(sequenceNumber)
            };
            AddAndCheck(block, dataAdapter, message, first, last);
        }

        private void AddAndCheck(CachedMessageBlock<TestCachedMessage> block, ICacheDataAdapter<TestQueueMessage, TestCachedMessage> dataAdapter, TestQueueMessage message, int first, int last)
        {
            Assert.Equal(first, block.OldestMessageIndex);
            Assert.Equal(last, block.NewestMessageIndex);
            Assert.True(block.HasCapacity);

            block.Add(message, DateTime.UtcNow, dataAdapter);
            last++;

            Assert.Equal(first > last, block.IsEmpty);
            Assert.Equal(last + 1 < TestBlockSize, block.HasCapacity);
            Assert.Equal(first, block.OldestMessageIndex);
            Assert.Equal(last, block.NewestMessageIndex);

            Assert.True(block.GetSequenceToken(last, dataAdapter).Equals(message.SequenceToken));
        }

        private void RemoveAndCheck(CachedMessageBlock<TestCachedMessage> block, int first, int last)
        {
            Assert.Equal(first, block.OldestMessageIndex);
            Assert.Equal(last, block.NewestMessageIndex);
            Assert.False(block.IsEmpty);
            Assert.Equal(last + 1 < TestBlockSize, block.HasCapacity);

            Assert.True(block.Remove());

            first++;
            Assert.Equal(first > last, block.IsEmpty);
            Assert.Equal(last + 1 < TestBlockSize, block.HasCapacity);
            Assert.Equal(first, block.OldestMessageIndex);
            Assert.Equal(last, block.NewestMessageIndex);
        }
    }
}
