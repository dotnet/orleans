﻿
using System;
using Orleans.Providers.Streams.Common;
using Xunit;

namespace UnitTests.OrleansRuntime.Streams
{
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
                return new FixedSizeBuffer(TestBlockSize) {Pool = this};
            }

            public void Free(FixedSizeBuffer resource)
            {
                Freed++;
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void EmptyBlockGetSegmentTooLargeBvt()
        {
            IObjectPool<FixedSizeBuffer> pool = new MyTestPooled();
            FixedSizeBuffer buffer = pool.Allocate();
            ArraySegment<byte> segment;
            Assert.False(buffer.TryGetSegment(TestBlockSize + 1, out segment), "Should not be able to get segement that is bigger than block.");
           Assert.Null(segment.Array);
            Assert.Equal(0, segment.Offset);
            Assert.Equal(0, segment.Count);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void EmptyBlockTryGetMaxSegmentBvt()
        {
            IObjectPool<FixedSizeBuffer> pool = new MyTestPooled();
            FixedSizeBuffer buffer = pool.Allocate();
            ArraySegment<byte> segment;
            Assert.True(buffer.TryGetSegment(TestBlockSize, out segment), "Should be able to get segement of block size.");
            Assert.NotNull(segment.Array);
            Assert.Equal(0, segment.Offset);
            Assert.Equal(TestBlockSize, segment.Count);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void FillBlockTestBvt()
        {
            IObjectPool<FixedSizeBuffer> pool = new MyTestPooled();
            FixedSizeBuffer buffer = pool.Allocate();
            ArraySegment<byte> segment;
            for (int i = 0; i < TestBlockSize; i++)
            {
                Assert.True(buffer.TryGetSegment(1, out segment), String.Format("Should be able to get {0}th segement of size 1.", i + 1));
                Assert.Equal(i, segment.Offset);
                Assert.Equal(1, segment.Count);
            }
            Assert.False(buffer.TryGetSegment(1, out segment), String.Format("Should be able to get {0}th segement of size 1.", TestBlockSize + 1));
           Assert.Null(segment.Array);
            Assert.Equal(0, segment.Offset);
            Assert.Equal(0, segment.Count);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void PurgeTestBvt()
        {
            var myTestPool = new MyTestPooled();
            IObjectPool<FixedSizeBuffer> pool = myTestPool;
            FixedSizeBuffer buffer = pool.Allocate();
            buffer.SetPurgeAction(request => MyTestPurge(request, buffer));
            buffer.SignalPurge();
            Assert.Equal(1, myTestPool.Allocated);
            Assert.Equal(1, myTestPool.Freed);
        }

        private void MyTestPurge(IDisposable resource, FixedSizeBuffer actualBuffer)
        {
            Assert.Equal<object>(resource, actualBuffer);
            resource.Dispose();
        }
    }
}
