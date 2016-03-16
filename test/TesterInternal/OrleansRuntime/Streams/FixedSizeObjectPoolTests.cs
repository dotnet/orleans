
using System;
using System.Linq;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans.Providers.Streams.Common;
using Xunit;

namespace UnitTests.OrleansRuntime.Streams
{
    public class FixedSizeObjectPoolTests
    {
        private class Accumulator
        {
            public int CurrentlyAllocated { get; set; }
            public int MaxAllocated { get; set; }

        }
        private class TestPooledResource : PooledResource<TestPooledResource>
        {
            private readonly Accumulator accumulator;

            public TestPooledResource(IObjectPool<TestPooledResource> pool, Accumulator accumulator)
                : base(pool)
            {
                this.accumulator = accumulator;
                this.accumulator.CurrentlyAllocated++;
                this.accumulator.MaxAllocated = Math.Max(this.accumulator.MaxAllocated, this.accumulator.CurrentlyAllocated);
            }

            public override void OnResetState()
            {
                this.accumulator.CurrentlyAllocated--;
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void Alloc1Free1Test()
        {
            var accumulator = new Accumulator();
            IObjectPool<TestPooledResource> pool = new FixedSizeObjectPool<TestPooledResource>(10, p => new TestPooledResource(p, accumulator));

            // Allocate and free 10 items
            for (int i = 0; i < 10; i++)
            {
                TestPooledResource resource = pool.Allocate();
                resource.Dispose();
            }
            // only 1 at a time was ever allocated, so max allocated should be 1
            Assert.AreEqual(1, accumulator.MaxAllocated);
            Assert.AreEqual(0, accumulator.CurrentlyAllocated);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void Alloc10Free1Test()
        {
            var accumulator = new Accumulator();
            IObjectPool<TestPooledResource> pool = new FixedSizeObjectPool<TestPooledResource>(10, p => new TestPooledResource(p, accumulator));

            // Allocate 10 items
            var resources = Enumerable.Range(0, 10).Select(i => pool.Allocate()).ToList();

            // free 10
            resources.ForEach(r => r.Dispose());

            // pool was pre-populated with 10, so max allocated should be 10
            Assert.AreEqual(10, accumulator.MaxAllocated);
            Assert.AreEqual(0, accumulator.CurrentlyAllocated);

            // Allocate and free 10 items
            for (int i = 0; i < 10; i++)
            {
                TestPooledResource resource = pool.Allocate();
                resource.Dispose();
            }

            // only 1 at a time was ever allocated, but pool was pre-populated with 10, so max allocated should be 10
            Assert.AreEqual(10, accumulator.MaxAllocated);
            Assert.AreEqual(0, accumulator.CurrentlyAllocated);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void Alloc50Max10Test()
        {
            var accumulator = new Accumulator();
            IObjectPool<TestPooledResource> pool = new FixedSizeObjectPool<TestPooledResource>(10, p => new TestPooledResource(p, accumulator));

            // Allocate 50 items
            var resources = Enumerable.Range(0, 50).Select(i => pool.Allocate()).ToList();

            // pool was set to max of 10, so max allocated should be 10
            Assert.AreEqual(10, accumulator.MaxAllocated);
            Assert.AreEqual(10, accumulator.CurrentlyAllocated);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void Alloc50Max10DoubleFreeTest()
        {
            var accumulator = new Accumulator();
            IObjectPool<TestPooledResource> pool = new FixedSizeObjectPool<TestPooledResource>(10, p => new TestPooledResource(p, accumulator));

            // Allocate 50 items
            var resources = Enumerable.Range(0, 50).Select(i => pool.Allocate()).ToList();

            // pool was set to max of 10, so max allocated should be 10
            Assert.AreEqual(10, accumulator.MaxAllocated);
            Assert.AreEqual(10, accumulator.CurrentlyAllocated);

            // free 50, note that 40 of these should already be freeded
            resources.ForEach(r => r.Dispose());
            Assert.AreEqual(10, accumulator.MaxAllocated);
            Assert.AreEqual(0, accumulator.CurrentlyAllocated);
        }
    }
}
