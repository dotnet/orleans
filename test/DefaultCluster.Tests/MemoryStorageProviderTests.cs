using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Storage;
using Orleans.Storage.Internal;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.StorageTests
{
    [TestCategory("Storage"), TestCategory("MemoryStore")]
    public class MemoryStorageProviderTests : HostedTestClusterEnsureDefaultStarted
    {
        public MemoryStorageProviderTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        [Fact, TestCategory("BVT")]
        public async Task MemoryStorageProvider_RestoreStateTest()
        {
            var grainWithState = this.GrainFactory.GetGrain<IInitialStateGrain>(0);
            Assert.NotNull(await grainWithState.GetNames());
        }

        [Fact, TestCategory("BVT")]
        public async Task MemoryStorageProvider_NullState()
        {
            var grainWithState = this.GrainFactory.GetGrain<INullStateGrain>(0);
            Assert.NotNull(await grainWithState.GetState());

            await grainWithState.SetStateAndDeactivate(null);
            await grainWithState.SetStateAndDeactivate(new NullableState { Name = "Thrall" });
        }

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

        [Fact, TestCategory("BVT")]
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
            var ex = await Assert.ThrowsAsync<MemoryStorageEtagMismatchException>(() => memoryStorageGrain.WriteStateAsync("grain", "id", TestGrainState.CreateWithEtag(null)));

            // try to write new state with different etag;
            ex = await Assert.ThrowsAsync<MemoryStorageEtagMismatchException>(() => memoryStorageGrain.WriteStateAsync("grain", "id", TestGrainState.CreateWithEtag(newEtag+"a")));

            // Write new state with good etag;
            string latestEtag = await memoryStorageGrain.WriteStateAsync("grain", "id", TestGrainState.CreateWithEtag(newEtag));
            Assert.NotNull(latestEtag);

            // try delete state with null etag
            ex = await Assert.ThrowsAsync<MemoryStorageEtagMismatchException>(() => memoryStorageGrain.DeleteStateAsync("grain", "id", null));

            // try delete state with wrong etag
            ex = await Assert.ThrowsAsync<MemoryStorageEtagMismatchException>(() => memoryStorageGrain.DeleteStateAsync("grain", "id", latestEtag+"a"));

            // delete state
            await memoryStorageGrain.DeleteStateAsync("grain", "id", latestEtag);

            // Read grain deleted grain state, should be safe, but return nothing.
            grainState = await memoryStorageGrain.ReadStateAsync("grain", "id");
            Assert.Null(grainState);

            // try delete already deleted grain state
            ex = await Assert.ThrowsAsync<MemoryStorageEtagMismatchException>(() => memoryStorageGrain.DeleteStateAsync("grain", "id", latestEtag));

            // try to write state to deleted state.
            ex = await Assert.ThrowsAsync<MemoryStorageEtagMismatchException>(() => memoryStorageGrain.WriteStateAsync("grain", "id", TestGrainState.CreateWithEtag(latestEtag)));

            // Make sure we can write new state to a deleted state
            await memoryStorageGrain.WriteStateAsync("grain", "id", TestGrainState.CreateWithEtag(null));
        }

        [Serializable]
        [GenerateSerializer]
        public class TestGrainState : IGrainState
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

            [Id(0)]
            public object State { get; set; }

            public Type Type => typeof(int);

            [Id(1)]
            public string ETag { get; set; }

            [Id(2)]
            public bool RecordExists { get; set; }
        }
    }
}