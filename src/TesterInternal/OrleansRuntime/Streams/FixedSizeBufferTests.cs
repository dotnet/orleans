
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Providers.Streams.Common;

namespace UnitTests.OrleansRuntime.Streams
{
    [TestClass]
    public class FixedSizeBufferTests
    {
        private const int TestBlockSize = 100;

        private class MyTestPooled : IObjectPool<FixedSizeBuffer>
        {
            public int Allocated { get; private set; }
            public int Freed { get; private set; }

            public FixedSizeBuffer Allocate()
            {
                Allocated++;
                return new FixedSizeBuffer(TestBlockSize, this);
            }

            public void Free(FixedSizeBuffer resource)
            {
                Freed++;
            }
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Streaming")]
        public void EmptyBlockGetSegmentTooLargeBvt()
        {
            IObjectPool<FixedSizeBuffer> pool = new MyTestPooled();
            FixedSizeBuffer buffer = pool.Allocate();
            ArraySegment<byte> segment;
            Assert.IsFalse(buffer.TryGetSegment(TestBlockSize + 1, out segment), "Should not be able to get segement that is bigger than block.");
            Assert.IsNull(segment.Array);
            Assert.AreEqual(0, segment.Offset);
            Assert.AreEqual(0, segment.Count);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Streaming")]
        public void EmptyBlockTryGetMaxSegmentBvt()
        {
            IObjectPool<FixedSizeBuffer> pool = new MyTestPooled();
            FixedSizeBuffer buffer = pool.Allocate();
            ArraySegment<byte> segment;
            Assert.IsTrue(buffer.TryGetSegment(TestBlockSize, out segment), "Should be able to get segement of block size.");
            Assert.IsNotNull(segment.Array);
            Assert.AreEqual(0, segment.Offset);
            Assert.AreEqual(TestBlockSize, segment.Count);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Streaming")]
        public void FillBlockTestBvt()
        {
            IObjectPool<FixedSizeBuffer> pool = new MyTestPooled();
            FixedSizeBuffer buffer = pool.Allocate();
            ArraySegment<byte> segment;
            for (int i = 0; i < TestBlockSize; i++)
            {
                Assert.IsTrue(buffer.TryGetSegment(1, out segment), String.Format("Should be able to get {0}th segement of size 1.", i + 1));
                Assert.AreEqual(i, segment.Offset);
                Assert.AreEqual(1, segment.Count);
            }
            Assert.IsFalse(buffer.TryGetSegment(1, out segment), String.Format("Should be able to get {0}th segement of size 1.", TestBlockSize + 1));
            Assert.IsNull(segment.Array);
            Assert.AreEqual(0, segment.Offset);
            Assert.AreEqual(0, segment.Count);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Streaming")]
        public void PurgeTestBvt()
        {
            var myTestPool = new MyTestPooled();
            IObjectPool<FixedSizeBuffer> pool = myTestPool;
            FixedSizeBuffer buffer = pool.Allocate();
            buffer.SetPurgeAction(request => MyTestPurge(request, buffer));
            buffer.SignalPurge();
            Assert.AreEqual(1, myTestPool.Allocated);
            Assert.AreEqual(1, myTestPool.Freed);
        }

        private void MyTestPurge(IDisposable resource, FixedSizeBuffer actualBuffer)
        {
            Assert.AreEqual<object>(resource, actualBuffer);
            resource.Dispose();
        }
    }
}
