using Orleans.Storage;
using Orleans.Storage.Internal;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.StorageTests
{
    /// <summary>
    /// Tests for the Orleans Memory Storage Provider.
    /// The memory storage provider stores grain state in memory (non-persistent)
    /// and is typically used for development, testing, and scenarios where
    /// state persistence across restarts is not required.
    /// </summary>
    [TestCategory("Storage"), TestCategory("MemoryStore")]
    public class MemoryStorageProviderTests : HostedTestClusterEnsureDefaultStarted
    {
        public MemoryStorageProviderTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        /// <summary>
        /// Tests that grains can restore their initial state from storage.
        /// Verifies the basic contract that grain state is loaded during activation
        /// and that the storage provider properly initializes state objects.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task MemoryStorageProvider_RestoreStateTest()
        {
            var grainWithState = this.GrainFactory.GetGrain<IInitialStateGrain>(0);
            Assert.NotNull(await grainWithState.GetNames());
        }

        /// <summary>
        /// Tests handling of null state values in the storage provider.
        /// Verifies that grains can store and retrieve null state without errors,
        /// and can transition between null and non-null state values.
        /// This is important for optional state scenarios.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task MemoryStorageProvider_NullState()
        {
            var grainWithState = this.GrainFactory.GetGrain<INullStateGrain>(0);
            Assert.NotNull(await grainWithState.GetState());

            await grainWithState.SetStateAndDeactivate(null);
            await grainWithState.SetStateAndDeactivate(new NullableState { Name = "Thrall" });
        }

        /// <summary>
        /// Tests basic write and read operations for grain state.
        /// Verifies that state changes are properly persisted in memory
        /// and can be retrieved, demonstrating the fundamental storage
        /// provider contract for state management.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task MemoryStorageProvider_WriteReadStateTest()
        {
            var grainWithState = this.GrainFactory.GetGrain<IInitialStateGrain>(0);

            List<string> names = await grainWithState.GetNames();
            Assert.NotNull(names);
            Assert.Empty(names);

            // first write
            await grainWithState.AddName("Bob");
            names = await grainWithState.GetNames();
            Assert.NotNull(names);
            Assert.Single(names);
            Assert.Equal("Bob", names[0]);

            // secodn write
            await grainWithState.AddName("Alice");
            names = await grainWithState.GetNames();
            Assert.NotNull(names);
            Assert.Equal(2, names.Count);
            Assert.Equal("Bob", names[0]);
            Assert.Equal("Alice", names[1]);
        }

        /// <summary>
        /// Tests ETag (entity tag) enforcement in the memory storage provider.
        /// ETags provide optimistic concurrency control by ensuring updates only succeed
        /// if the state hasn't changed since it was read. This test verifies:
        /// - Null ETags work for initial writes
        /// - Mismatched ETags cause appropriate exceptions
        /// - Correct ETags allow updates
        /// - Deleted state requires null ETags for new writes
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task MemoryStorageGrainEnforcesEtagsTest()
        {
            var memoryStorageGrain = this.GrainFactory.GetGrain<IMemoryStorageGrain>(Random.Shared.Next());

            // Delete grain state from empty grain, should be safe.
            await memoryStorageGrain.DeleteStateAsync<object>("grainStoreKey", "eTag");

            // Read grain state from empty grain, should be safe, but return nothing.
            var grainState = await memoryStorageGrain.ReadStateAsync<object>("grainStoreKey");
            Assert.Null(grainState);

            // write state with etag, when there is nothing in storage.  Most storage should fail this, but memory storage should succeed.
            await memoryStorageGrain.WriteStateAsync("grainId", TestGrainState.CreateRandom());

            // write new state with null etag
            string newEtag = await memoryStorageGrain.WriteStateAsync("id", TestGrainState.CreateWithEtag(null));
            Assert.NotNull(newEtag);

            // try to write new state with null etag;
            var ex = await Assert.ThrowsAsync<MemoryStorageEtagMismatchException>(() => memoryStorageGrain.WriteStateAsync("id", TestGrainState.CreateWithEtag(null)));

            // try to write new state with different etag;
            ex = await Assert.ThrowsAsync<MemoryStorageEtagMismatchException>(() => memoryStorageGrain.WriteStateAsync("id", TestGrainState.CreateWithEtag(newEtag + "a")));

            // Write new state with good etag;
            string latestEtag = await memoryStorageGrain.WriteStateAsync("id", TestGrainState.CreateWithEtag(newEtag));
            Assert.NotNull(latestEtag);

            // try delete state with null etag
            ex = await Assert.ThrowsAsync<MemoryStorageEtagMismatchException>(() => memoryStorageGrain.DeleteStateAsync<object>("id", null));

            // try delete state with wrong etag
            ex = await Assert.ThrowsAsync<MemoryStorageEtagMismatchException>(() => memoryStorageGrain.DeleteStateAsync<object>("id", latestEtag + "a"));

            // delete state
            await memoryStorageGrain.DeleteStateAsync<object>("id", latestEtag);

            // Read grain deleted grain state, should be safe, but return nothing.
            grainState = await memoryStorageGrain.ReadStateAsync<object>("id");
            Assert.Null(grainState);

            // try delete already deleted grain state
            ex = await Assert.ThrowsAsync<MemoryStorageEtagMismatchException>(() => memoryStorageGrain.DeleteStateAsync<object>("id", latestEtag));

            // try to write state to deleted state.
            ex = await Assert.ThrowsAsync<MemoryStorageEtagMismatchException>(() => memoryStorageGrain.WriteStateAsync("id", TestGrainState.CreateWithEtag(latestEtag)));

            // Make sure we can write new state to a deleted state
            await memoryStorageGrain.WriteStateAsync("id", TestGrainState.CreateWithEtag(null));
        }

        [Serializable]
        [GenerateSerializer]
        public class TestGrainState : IGrainState<object>
        {
            public static IGrainState<object> CreateRandom()
            {
                return new TestGrainState
                {
                    State = Random.Shared.Next(),
                    ETag = Random.Shared.Next().ToString()
                };
            }

            public static IGrainState<object> CreateWithEtag(string eTag)
            {
                return new TestGrainState
                {
                    State = Random.Shared.Next(),
                    ETag = eTag
                };
            }

            [Id(0)]
            public object State { get; set; }

            [Id(1)]
            public string ETag { get; set; }

            [Id(2)]
            public bool RecordExists { get; set; }
        }
    }
}