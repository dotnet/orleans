using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;

namespace UnitTests.StorageTests
{
    [TestClass]
    public class MemoryStorageProviderTests : HostedTestClusterEnsureDefaultStarted
    {
        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Storage")]
        public async Task MemoryStorageProvider_RestoreStateTest()
        {
            var grainWithState = GrainClient.GrainFactory.GetGrain<IInitialStateGrain>(0);
            Assert.IsNotNull(await grainWithState.GetNames());
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Storage")]
        public async Task MemoryStorageProvider_WriteReadStateTest()
        {
            var grainWithState = GrainClient.GrainFactory.GetGrain<IInitialStateGrain>(0);

            List<string> names = await grainWithState.GetNames();
            Assert.IsNotNull(names);
            Assert.AreEqual(0, names.Count);

            // first write
            await grainWithState.AddName("Bob");
            names = await grainWithState.GetNames();
            Assert.IsNotNull(names);
            Assert.AreEqual(1, names.Count);
            Assert.AreEqual("Bob", names[0]);

            // secodn write
            await grainWithState.AddName("Alice");
            names = await grainWithState.GetNames();
            Assert.IsNotNull(names);
            Assert.AreEqual(2, names.Count);
            Assert.AreEqual("Bob", names[0]);
            Assert.AreEqual("Alice", names[1]);
        }
    }
}