using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Storage;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.StorageTests
{
    public class MemoryStorageProviderTests : HostedTestClusterEnsureDefaultStarted
    {
        public MemoryStorageProviderTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Storage")]
        public async Task MemoryStorageProvider_RestoreStateTest()
        {
            var grainWithState = this.GrainFactory.GetGrain<IInitialStateGrain>(0);
            Assert.NotNull(await grainWithState.GetNames());
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Storage")]
        public async Task MemoryStorageProvider_WriteReadStateTest()
        {
            var grainWithState = this.GrainFactory.GetGrain<IInitialStateGrain>(0);

            List<string> names = await grainWithState.GetNames();
            Assert.NotNull(names);
            Assert.Equal(0, names.Count);

            // first write
            await grainWithState.AddName("Bob");
            names = await grainWithState.GetNames();
            Assert.NotNull(names);
            Assert.Equal(1, names.Count);
            Assert.Equal("Bob", names[0]);

            // secodn write
            await grainWithState.AddName("Alice");
            names = await grainWithState.GetNames();
            Assert.NotNull(names);
            Assert.Equal(2, names.Count);
            Assert.Equal("Bob", names[0]);
            Assert.Equal("Alice", names[1]);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Storage")]
        public async Task MemoryStorageGrainEnforcesEtagsTest()
        {
            var memoryStorageGrain = this.GrainFactory.GetGrain<IMemoryStorageGrain>(random.Next());

            // Delete grain state from empty grain, should be safe.
            await memoryStorageGrain.DeleteStateAsync("stateStore", "grainStoreKey", "eTag");

            // Read grain state from empty grain, should be safe, but return nothing.
            IGrainState grainState = await memoryStorageGrain.ReadStateAsync("stateStore", "grainStoreKey");
            Assert.Null(grainState);

            // write state with etag, when there is nothing in storage.  Most storage should fail this, but memory storage should succeed.
            await memoryStorageGrain.WriteStateAsync("grainType", "grainId", TestGrainState.CreateRandom());

            // write new state with null etag
            string newEtag = await memoryStorageGrain.WriteStateAsync("grain", "id", TestGrainState.CreateWithEtag(null));
            Assert.NotNull(newEtag);

            // try to write new state with null etag;
            await Assert.ThrowsAsync<InconsistentStateException>(() => memoryStorageGrain.WriteStateAsync("grain", "id", TestGrainState.CreateWithEtag(null)));

            // try to write new state with different etag;
            await Assert.ThrowsAsync<InconsistentStateException>(() => memoryStorageGrain.WriteStateAsync("grain", "id", TestGrainState.CreateWithEtag(newEtag+"a")));

            // Write new state with good etag;
            string latestEtag = await memoryStorageGrain.WriteStateAsync("grain", "id", TestGrainState.CreateWithEtag(newEtag));
            Assert.NotNull(latestEtag);

            // try delete state with null etag
            await Assert.ThrowsAsync<InconsistentStateException>(() => memoryStorageGrain.DeleteStateAsync("grain", "id", null));

            // try delete state with wrong etag
            await Assert.ThrowsAsync<InconsistentStateException>(() => memoryStorageGrain.DeleteStateAsync("grain", "id", latestEtag+"a"));

            // delete state
            await memoryStorageGrain.DeleteStateAsync("grain", "id", latestEtag);

            // Read grain deleted grain state, should be safe, but return nothing.
            grainState = await memoryStorageGrain.ReadStateAsync("grain", "id");
            Assert.Null(grainState);

            // try delete already deleted grain state
            await Assert.ThrowsAsync<InconsistentStateException>(() => memoryStorageGrain.DeleteStateAsync("grain", "id", latestEtag));

            // try to write state to deleted state.
            await Assert.ThrowsAsync<InconsistentStateException>(() => memoryStorageGrain.WriteStateAsync("grain", "id", TestGrainState.CreateWithEtag(latestEtag)));

            // Make sure we can write new state to a deleted state
            await memoryStorageGrain.WriteStateAsync("grain", "id", TestGrainState.CreateWithEtag(null));
        }

        [Serializable]
        private class TestGrainState : IGrainState
        {
            public static IGrainState CreateRandom()
            {
                return new TestGrainState
                {
                    State = random.Next(),
                    ETag = random.Next().ToString()
                };
            }

            public static IGrainState CreateWithEtag(string eTag)
            {
                return new TestGrainState
                {
                    State = random.Next(),
                    ETag = eTag
                };
            }

            public object State { get; set; }
            public string ETag { get; set; }
        }
    }
}