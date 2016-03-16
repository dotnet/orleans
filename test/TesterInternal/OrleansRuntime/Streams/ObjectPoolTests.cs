
using System;
using System.Linq;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans.Providers.Streams.Common;
using Xunit;

namespace UnitTests.OrleansRuntime.Streams
{
    public class ObjectPoolTests
    {
        private class Accumulator
        {
            public int CurrentlyAllocated { get; set; }
            public int MaxAllocated { get; set; }

        }
        private class TestPooledResource : PooledResource<TestPooledResource>
        {
            private readonly Accumulator accumulator;

            public TestPooledResource(IObjectPool<TestPooledResource> pool, Accumulator accumulator) : base(pool)
            {
                this.accumulator = accumulator;
                this.accumulator.CurrentlyAllocated++;
                this.accumulator.MaxAllocated = Math.Max(this.accumulator.MaxAllocated, this.accumulator.CurrentlyAllocated);
            }

            public override void OnResetState()
            {
                accumulator.CurrentlyAllocated--;
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void Alloc1Free1Test()
        {
            var accumulator = new Accumulator();
            IObjectPool<TestPooledResource> pool = new ObjectPool<TestPooledResource>(p => new TestPooledResource(p, accumulator), 0);
            // Allocate and free 10 items
            for (int i = 0; i < 10; i++)
            {
                TestPooledResource resource = pool.Allocate();
                resource.Dispose();
            }
            // only 1 at a time was ever allocated, so max allocated should be 1
            Assert.AreEqual(1, accumulator.MaxAllocated);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void Alloc10Free1Test()
        {
            var accumulator = new Accumulator();
            IObjectPool<TestPooledResource> pool = new ObjectPool<TestPooledResource>(p => new TestPooledResource(p,accumulator), 10);

            // Allocate 10 items
            var resources = Enumerable.Range(0, 10).Select(i => pool.Allocate()) .ToList();

            // free 10
            resources.ForEach(r => r.Dispose());

            // pool was pre-populated with 10, so max allocated should be 10
            Assert.AreEqual(10, accumulator.MaxAllocated);

            // Allocate and free 10 items
            for (int i = 0; i < 10; i++)
            {
                TestPooledResource resource = pool.Allocate();
                resource.Dispose();
            }

            // only 1 at a time was ever allocated, but pool was pre-populated with 10, so max allocated should be 10
            Assert.AreEqual(10, accumulator.MaxAllocated);
        }
    }
}
