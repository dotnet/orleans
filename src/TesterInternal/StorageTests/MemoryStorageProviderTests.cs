using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
namespace UnitTests.StorageTests
{
    [TestClass]
    public class MemoryStorageProviderTests : UnitTestSiloHost
    {
        [TestMethod]
        public async Task MemoryStorageProvider_RestoreStateTest()
        {
            var grainWithState = GrainClient.GrainFactory.GetGrain<IInitialStateGrain>(0);
            Assert.IsNotNull(await grainWithState.GetNames());
        }
    }
}