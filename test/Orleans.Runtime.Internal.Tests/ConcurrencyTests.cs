using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.ConcurrencyTests
{
    /// <summary>
    /// Tests for Orleans grain concurrency features and read-only methods.
    /// 
    /// Orleans provides several concurrency models for grains:
    /// - Default: Turn-based concurrency (one request at a time)
    /// - [ReadOnly]: Methods can execute concurrently with other methods
    /// - [AlwaysInterleave]: All methods can interleave
    /// - [Reentrant]: Grain can process new requests while awaiting
    /// 
    /// These tests focus on [ReadOnly] methods, which allow multiple read operations
    /// to execute concurrently while maintaining data safety. This is crucial for:
    /// - Improving throughput for read-heavy workloads
    /// - Reducing latency when multiple clients read the same data
    /// - Maintaining grain state consistency
    /// </summary>
    public class ConcurrencyTests : OrleansTestingBase, IClassFixture<ConcurrencyTests.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
        }

        public ConcurrencyTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        /// <summary>
        /// Tests that methods marked with [ReadOnly] can execute concurrently.
        /// Sends 5 concurrent requests to a ReadOnly method and verifies they
        /// all complete successfully without being serialized.
        /// If these methods were not concurrent, they would execute sequentially,
        /// taking much longer to complete.
        /// </summary>
        [Fact, TestCategory("Functional"), TestCategory("ReadOnly"), TestCategory("AsynchronyPrimitives")]
        public async Task ConcurrencyTest_ReadOnly()
        {
            IConcurrentGrain first = this.fixture.GrainFactory.GetGrain<IConcurrentGrain>(GetRandomGrainId());
            await first.Initialize(0);

            List<Task> promises = new List<Task>();
            for (int i = 0; i < 5; i++)
            {
                Task p = first.A();
                promises.Add(p);
            }
            await Task.WhenAll(promises);
        }

        /// <summary>
        /// Stress test for concurrent execution with collection return values.
        /// Verifies that:
        /// - Multiple concurrent calls can safely return collections
        /// - Orleans properly handles deep copying of return values
        /// - No data corruption occurs under high concurrency (2000 iterations × 20 concurrent calls)
        /// 
        /// This test is important because returning mutable collections from concurrent
        /// methods could lead to race conditions if not properly handled by the runtime.
        /// </summary>
        [Fact, TestCategory("Functional"), TestCategory("ReadOnly"), TestCategory("AsynchronyPrimitives")]
        public async Task ConcurrencyTest_ModifyReturnList()
        {
            IConcurrentGrain grain = this.fixture.GrainFactory.GetGrain<IConcurrentGrain>(GetRandomGrainId());

            Task<List<int>>[] ll = new Task<List<int>>[20];
            for (int i = 0; i < 2000; i++)
            {
                for (int j = 0; j < ll.Length; j++)
                    ll[j] = grain.ModifyReturnList_Test();

                await Task.WhenAll(ll);
            }
        }
    }
}
