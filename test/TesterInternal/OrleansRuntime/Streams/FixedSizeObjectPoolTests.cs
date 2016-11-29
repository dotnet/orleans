
using System.Linq;
using Orleans.Providers.Streams.Common;
using Xunit;

namespace UnitTests.OrleansRuntime.Streams
{
    public class FixedSizeObjectPoolTests
    {
        private class Accumulator
        {
            public int MaxAllocated { get; set; }

        }
        private class TestPooledResource : PooledResource<TestPooledResource>
        {
            public TestPooledResource(Accumulator accumulator)
            {
                accumulator.MaxAllocated++;
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void Alloc1Free1Test()
        {
            var accumulator = new Accumulator();
            IObjectPool<TestPooledResource> pool = new FixedSizeObjectPool<TestPooledResource>(10, () => new TestPooledResource(accumulator));

            // Allocate and free 20 items
            for (int i = 0; i < 20; i++)
            {
                TestPooledResource resource = pool.Allocate();
                resource.Dispose();
            }
            // only 1 at a time was ever allocated, so max allocated should be 1
            Assert.Equal(1, accumulator.MaxAllocated);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void Alloc10Free1Test()
        {
            var accumulator = new Accumulator();
            IObjectPool<TestPooledResource> pool = new FixedSizeObjectPool<TestPooledResource>(10, () => new TestPooledResource(accumulator));

            // Allocate 10 items
            var resources = Enumerable.Range(0, 10).Select(i => pool.Allocate()).ToList();

            // free 10
            resources.ForEach(r => r.Dispose());

            // pool was pre-populated with 10, so max allocated should be 10
            Assert.Equal(10, accumulator.MaxAllocated);

            // Allocate and free 10 items
            for (int i = 0; i < 10; i++)
            {
                TestPooledResource resource = pool.Allocate();
                resource.Dispose();
            }

            // only 1 at a time was ever allocated, but pool was pre-populated with 10, so max allocated should be 10
            Assert.Equal(10, accumulator.MaxAllocated);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void Alloc50Max10Test()
        {
            var accumulator = new Accumulator();
            IObjectPool<TestPooledResource> pool = new FixedSizeObjectPool<TestPooledResource>(10, () => new TestPooledResource(accumulator));

            // Allocate 50 items
            var resources = Enumerable.Range(0, 50).Select(i => pool.Allocate()).ToList();

            // pool was set to max of 10, so max allocated should be 10
            Assert.Equal(10, accumulator.MaxAllocated);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void Alloc50Max10DoubleFreeTest()
        {
            var accumulator = new Accumulator();
            IObjectPool<TestPooledResource> pool = new FixedSizeObjectPool<TestPooledResource>(10, () => new TestPooledResource(accumulator));

            // Allocate 50 items
            var resources = Enumerable.Range(0, 50).Select(i => pool.Allocate()).ToList();

            // pool was set to max of 10, so max allocated should be 10
            Assert.Equal(10, accumulator.MaxAllocated);

            // free 50, note that 40 of these should already be freeded
            resources.ForEach(r => r.Dispose());
            Assert.Equal(10, accumulator.MaxAllocated);
        }
    }
}
