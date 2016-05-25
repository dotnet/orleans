using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Xunit;
using Tester;

namespace UnitTests.StorageTests
{
    public class MemoryStorageProviderTests : HostedTestClusterEnsureDefaultStarted
    {
        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Storage")]
        public async Task MemoryStorageProvider_RestoreStateTest()
        {
            var grainWithState = GrainClient.GrainFactory.GetGrain<IInitialStateGrain>(0);
            Assert.NotNull(await grainWithState.GetNames());
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Storage")]
        public async Task MemoryStorageProvider_WriteReadStateTest()
        {
            var grainWithState = GrainClient.GrainFactory.GetGrain<IInitialStateGrain>(0);

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
    }
}