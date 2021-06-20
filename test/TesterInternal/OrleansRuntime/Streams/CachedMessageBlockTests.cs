
using System;
using System.Collections.Generic;
using Orleans;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
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
            public EventSequenceTokenV2 SequenceToken { get; set; }
        }

        [GenerateSerializer]
        public class TestBatchContainer : IBatchContainer
        {
            [Id(0)]
            public StreamId StreamId { get; set; }

            [Id(1)]
            public StreamSequenceToken SequenceToken { get; set; }

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
                return new TestBatchContainer()
                {
                    StreamId = cachedMessage.StreamId,
                    SequenceToken = new EventSequenceToken(cachedMessage.SequenceNumber, cachedMessage.EventIndex)
                };
            }

            public StreamSequenceToken GetSequenceToken(ref CachedMessage cachedMessage)
            {
                return new EventSequenceTokenV2(cachedMessage.SequenceNumber, cachedMessage.EventIndex);
            }
        }

        private StreamPosition GetStreamPosition(TestQueueMessage queueMessage)
        {
            var streamId = StreamId.Create(null, queueMessage.StreamGuid);
            StreamSequenceToken sequenceToken = queueMessage.SequenceToken;
            return new StreamPosition(streamId, sequenceToken);
        }

        private CachedMessage QueueMessageToCachedMessage(TestQueueMessage queueMessage, DateTime dequeueTimeUtc)
        {
            StreamPosition streamPosition = GetStreamPosition(queueMessage);
            return new CachedMessage
            {
                StreamId = streamPosition.StreamId,
                SequenceNumber = queueMessage.SequenceToken.SequenceNumber,
                EventIndex = queueMessage.SequenceToken.EventIndex,
            };
        }

        private class MyTestPooled : IObjectPool<CachedMessageBlock>
        {
            public CachedMessageBlock Allocate()
            {
                return new CachedMessageBlock(TestBlockSize){Pool = this};
            }

            public void Free(CachedMessageBlock resource)
            {
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void Add1Remove1Test()
        {
            IObjectPool<CachedMessageBlock> pool = new MyTestPooled();
            ICacheDataAdapter dataAdapter = new TestCacheDataAdapter();
            CachedMessageBlock block = pool.Allocate();

            AddAndCheck(block, dataAdapter, 0, -1);
            RemoveAndCheck(block, 0, 0);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void Add2Remove1UntilFull()
        {
            IObjectPool<CachedMessageBlock> pool = new MyTestPooled();
            ICacheDataAdapter dataAdapter = new TestCacheDataAdapter();
            CachedMessageBlock block = pool.Allocate();
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
            IObjectPool<CachedMessageBlock> pool = new MyTestPooled();
            ICacheDataAdapter dataAdapter = new TestCacheDataAdapter();
            CachedMessageBlock block = pool.Allocate();
            int last = -1;
            int sequenceNumber = 0;

            while (block.HasCapacity)
            {
                // add message to end of block
                AddAndCheck(block, dataAdapter, 0, last, sequenceNumber);
                last++;
                sequenceNumber += 2;
            }
            Assert.Equal(block.OldestMessageIndex, block.GetIndexOfFirstMessageLessThanOrEqualTo(new EventSequenceTokenV2(0)));
            Assert.Equal(block.OldestMessageIndex, block.GetIndexOfFirstMessageLessThanOrEqualTo(new EventSequenceTokenV2(1)));
            Assert.Equal(block.NewestMessageIndex, block.GetIndexOfFirstMessageLessThanOrEqualTo(new EventSequenceTokenV2(sequenceNumber - 2)));
            Assert.Equal(block.NewestMessageIndex - 1, block.GetIndexOfFirstMessageLessThanOrEqualTo(new EventSequenceTokenV2(sequenceNumber - 3)));
            Assert.Equal(50, block.GetIndexOfFirstMessageLessThanOrEqualTo(new EventSequenceTokenV2(sequenceNumber / 2)));
            Assert.Equal(50, block.GetIndexOfFirstMessageLessThanOrEqualTo(new EventSequenceTokenV2(sequenceNumber / 2 + 1)));
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void NextInStreamTest()
        {
            IObjectPool<CachedMessageBlock> pool = new MyTestPooled();
            ICacheDataAdapter dataAdapter = new TestCacheDataAdapter();
            CachedMessageBlock block = pool.Allocate();
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
                    SequenceToken = new EventSequenceTokenV2(sequenceNumber)
                };

                // add message to end of block
                AddAndCheck(block, dataAdapter, message, 0, last - 1);
                last++;
                sequenceNumber += 2;
            }

            // get index of first stream
            int streamIndex;
            Assert.True(block.TryFindFirstMessage(StreamId.Create(streams[0]), dataAdapter, out streamIndex));
            Assert.Equal(0, streamIndex);
            Assert.Equal(0, block.GetSequenceToken(streamIndex, dataAdapter).SequenceNumber);

            // find stream1 messages
            int iteration = 1;
            while (block.TryFindNextMessage(streamIndex + 1, StreamId.Create(streams[0]), dataAdapter, out streamIndex))
            {
                Assert.Equal(iteration * 2, streamIndex);
                Assert.Equal(iteration * 4, block.GetSequenceToken(streamIndex, dataAdapter).SequenceNumber);
                iteration++;
            }
            Assert.Equal(iteration, TestBlockSize / 2);

            // get index of first stream
            Assert.True(block.TryFindFirstMessage(StreamId.Create(streams[1]), dataAdapter, out streamIndex));
            Assert.Equal(1, streamIndex);
            Assert.Equal(2, block.GetSequenceToken(streamIndex, dataAdapter).SequenceNumber);

            // find stream1 messages
            iteration = 1;
            while (block.TryFindNextMessage(streamIndex + 1, StreamId.Create(streams[1]), dataAdapter, out streamIndex))
            {
                Assert.Equal(iteration * 2 + 1, streamIndex);
                Assert.Equal(iteration * 4 + 2, block.GetSequenceToken(streamIndex, dataAdapter).SequenceNumber);
                iteration++;
            }
            Assert.Equal(iteration, TestBlockSize / 2);
        }

        private void AddAndCheck(CachedMessageBlock block, ICacheDataAdapter dataAdapter, int first, int last, int sequenceNumber = 1)
        {
            var message = new TestQueueMessage
            {
                StreamGuid = StreamGuid,
                SequenceToken = new EventSequenceTokenV2(sequenceNumber)
            };
            AddAndCheck(block, dataAdapter, message, first, last);
        }

        private void AddAndCheck(CachedMessageBlock block, ICacheDataAdapter dataAdapter, TestQueueMessage message, int first, int last)
        {
            Assert.Equal(first, block.OldestMessageIndex);
            Assert.Equal(last, block.NewestMessageIndex);
            Assert.True(block.HasCapacity);

            block.Add(QueueMessageToCachedMessage(message, DateTime.UtcNow));
            last++;

            Assert.Equal(first > last, block.IsEmpty);
            Assert.Equal(last + 1 < TestBlockSize, block.HasCapacity);
            Assert.Equal(first, block.OldestMessageIndex);
            Assert.Equal(last, block.NewestMessageIndex);

            Assert.True(block.GetSequenceToken(last, dataAdapter).Equals(message.SequenceToken));
        }

        private void RemoveAndCheck(CachedMessageBlock block, int first, int last)
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
