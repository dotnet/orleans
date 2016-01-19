
using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace UnitTests.OrleansRuntime.Streams
{
    [TestClass]
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
            public string StreamNamespace { get { return null; }}
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


        private class TestCacheDataAdapter : ICacheDataAdapter<TestQueueMessage, TestCachedMessage>
        {
            public void QueueMessageToCachedMessage(ref TestCachedMessage cachedMessage, TestQueueMessage queueMessage)
            {
                cachedMessage.StreamGuid = queueMessage.StreamGuid;
                cachedMessage.SequenceNumber = queueMessage.SequenceToken.SequenceNumber;
                cachedMessage.EventIndex = queueMessage.SequenceToken.EventIndex;
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

            public int CompareCachedMessageToSequenceToken(ref TestCachedMessage cachedMessage, StreamSequenceToken token)
            {
                return GetSequenceToken(ref cachedMessage).CompareTo(token);
            }

            public bool IsInStream(ref TestCachedMessage cachedMessage, Guid streamGuid, string streamNamespace)
            {
                return cachedMessage.StreamGuid == streamGuid && streamNamespace == null;
            }

            public bool ShouldPurge(TestCachedMessage cachedMessage, IDisposable purgeRequest)
            {
                throw new NotImplementedException();
            }
        }

        private class MyTestPooled : IObjectPool<CachedMessageBlock<TestQueueMessage, TestCachedMessage>>
        {
            private readonly ICacheDataAdapter<TestQueueMessage, TestCachedMessage> cacheDataAdapter = new TestCacheDataAdapter();

            public CachedMessageBlock<TestQueueMessage, TestCachedMessage> Allocate()
            {
                return new CachedMessageBlock<TestQueueMessage, TestCachedMessage>(this, cacheDataAdapter, TestBlockSize);
            }

            public void Free(CachedMessageBlock<TestQueueMessage, TestCachedMessage> resource)
            {
            }
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Streaming")]
        public void Add1Remove1Test()
        {
            IObjectPool<CachedMessageBlock<TestQueueMessage, TestCachedMessage>> pool = new MyTestPooled();
            CachedMessageBlock<TestQueueMessage, TestCachedMessage> block = pool.Allocate();

            AddAndCheck(block, 0, -1);
            RemoveAndCheck(block, 0, 0);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Streaming")]
        public void Add2Remove1UntilFull()
        {
            IObjectPool<CachedMessageBlock<TestQueueMessage, TestCachedMessage>> pool = new MyTestPooled();
            CachedMessageBlock<TestQueueMessage, TestCachedMessage> block = pool.Allocate();
            int first = 0;
            int last = -1;

            while (block.HasCapacity)
            {
                // add message to end of block
                AddAndCheck(block, first, last);
                last++;
                if (!block.HasCapacity)
                {
                    continue;
                }
                // add message to end of block
                AddAndCheck(block, first, last);
                last++;
                // removed message from start of block
                RemoveAndCheck(block, first, last);
                first++;
            }
            Assert.AreEqual(TestBlockSize / 2, block.OldestMessageIndex);
            Assert.AreEqual(TestBlockSize - 1, block.NewestMessageIndex);
            Assert.IsFalse(block.IsEmpty);
            Assert.IsFalse(block.HasCapacity);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Streaming")]
        public void FirstMessageWithSequenceNumberTest()
        {
            IObjectPool<CachedMessageBlock<TestQueueMessage, TestCachedMessage>> pool = new MyTestPooled();
            CachedMessageBlock<TestQueueMessage, TestCachedMessage> block = pool.Allocate();
            int last = -1;
            int sequenceNumber = 0;

            while (block.HasCapacity)
            {
                // add message to end of block
                AddAndCheck(block, 0, last, sequenceNumber);
                last++;
                sequenceNumber += 2;
            }
            Assert.AreEqual(block.OldestMessageIndex, block.GetIndexOfFirstMessageLessThanOrEqualTo(new EventSequenceToken(0)));
            Assert.AreEqual(block.OldestMessageIndex, block.GetIndexOfFirstMessageLessThanOrEqualTo(new EventSequenceToken(1)));
            Assert.AreEqual(block.NewestMessageIndex, block.GetIndexOfFirstMessageLessThanOrEqualTo(new EventSequenceToken(sequenceNumber - 2)));
            Assert.AreEqual(block.NewestMessageIndex - 1, block.GetIndexOfFirstMessageLessThanOrEqualTo(new EventSequenceToken(sequenceNumber - 3)));
            Assert.AreEqual(50, block.GetIndexOfFirstMessageLessThanOrEqualTo(new EventSequenceToken(sequenceNumber / 2)));
            Assert.AreEqual(50, block.GetIndexOfFirstMessageLessThanOrEqualTo(new EventSequenceToken(sequenceNumber / 2 + 1)));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Streaming")]
        public void NextInStreamTest()
        {
            IObjectPool<CachedMessageBlock<TestQueueMessage, TestCachedMessage>> pool = new MyTestPooled();
            CachedMessageBlock<TestQueueMessage, TestCachedMessage> block = pool.Allocate();
            int last = 0;
            int sequenceNumber = 0;
            // define 2 streams
            var streams = new[] { Guid.NewGuid(), Guid.NewGuid() };

            // add both streams interleaved, until lock is full
            while (block.HasCapacity)
            {
                var stream = streams[last%2];
                var message = new TestQueueMessage
                {
                    StreamGuid = stream,
                    SequenceToken = new EventSequenceToken(sequenceNumber)
                };

                // add message to end of block
                AddAndCheck(block, message, 0, last - 1);
                last++;
                sequenceNumber += 2;
            }

            // get index of first stream
            int streamIndex;
            Assert.IsTrue(block.TryFindFirstMessage(streams[0], null, out streamIndex));
            Assert.AreEqual(0, streamIndex);
            Assert.AreEqual(0, (block.GetSequenceToken(streamIndex) as EventSequenceToken).SequenceNumber);

            // find stream1 messages
            int iteration = 1;
            while (block.TryFindNextMessage(streamIndex + 1, streams[0], null, out streamIndex))
            {
                Assert.AreEqual(iteration * 2, streamIndex);
                Assert.AreEqual(iteration * 4, (block.GetSequenceToken(streamIndex) as EventSequenceToken).SequenceNumber);
                iteration++;
            }
            Assert.AreEqual(iteration, TestBlockSize / 2);

            // get index of first stream
            Assert.IsTrue(block.TryFindFirstMessage(streams[1], null, out streamIndex));
            Assert.AreEqual(1, streamIndex);
            Assert.AreEqual(2, (block.GetSequenceToken(streamIndex) as EventSequenceToken).SequenceNumber);

            // find stream1 messages
            iteration = 1;
            while (block.TryFindNextMessage(streamIndex + 1, streams[1], null, out streamIndex))
            {
                Assert.AreEqual(iteration * 2 + 1, streamIndex);
                Assert.AreEqual(iteration * 4 + 2, (block.GetSequenceToken(streamIndex) as EventSequenceToken).SequenceNumber);
                iteration++;
            }
            Assert.AreEqual(iteration, TestBlockSize / 2);
        }

        private void AddAndCheck(CachedMessageBlock<TestQueueMessage, TestCachedMessage> block, int first, int last, int sequenceNumber = 1)
        {
            var message = new TestQueueMessage
            {
                StreamGuid = StreamGuid,
                SequenceToken = new EventSequenceToken(sequenceNumber)
            };
            AddAndCheck(block, message, first, last);
        }

        private void AddAndCheck(CachedMessageBlock<TestQueueMessage, TestCachedMessage> block, TestQueueMessage message, int first, int last)
        {
            Assert.AreEqual(first, block.OldestMessageIndex);
            Assert.AreEqual(last, block.NewestMessageIndex);
            Assert.IsTrue(block.HasCapacity);

            block.Add(message);
            last++;

            Assert.AreEqual(first > last, block.IsEmpty);
            Assert.AreEqual(last + 1 < TestBlockSize, block.HasCapacity);
            Assert.AreEqual(first, block.OldestMessageIndex);
            Assert.AreEqual(last, block.NewestMessageIndex);

            Assert.IsTrue(block.GetSequenceToken(last).Equals(message.SequenceToken));
        }

        private void RemoveAndCheck(CachedMessageBlock<TestQueueMessage, TestCachedMessage> block, int first, int last)
        {
            Assert.AreEqual(first, block.OldestMessageIndex);
            Assert.AreEqual(last, block.NewestMessageIndex);
            Assert.IsFalse(block.IsEmpty);
            Assert.AreEqual(last + 1 < TestBlockSize, block.HasCapacity);

            Assert.IsTrue(block.Remove());

            first++;
            Assert.AreEqual(first > last, block.IsEmpty);
            Assert.AreEqual(last + 1 < TestBlockSize, block.HasCapacity);
            Assert.AreEqual(first, block.OldestMessageIndex);
            Assert.AreEqual(last, block.NewestMessageIndex);
        }
    }
}
