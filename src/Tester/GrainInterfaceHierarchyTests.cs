using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using TestGrainInterfaces;
using UnitTests.Tester;

namespace Tester
{
    [TestClass]
    public class GrainInterfaceHierarchyTests : HostedTestClusterEnsureDefaultStarted
    {
        private T GetHierarchyGrain<T>() where T : IDoSomething, IGrainWithIntegerKey
        {
            return GrainFactory.GetGrain<T>(GetRandomGrainId());
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional")]
        public async Task DoSomethingGrainEmptyTest()
        {
            IDoSomethingEmptyGrain doSomething = GetHierarchyGrain<IDoSomethingEmptyGrain>();
            Assert.AreEqual(await doSomething.DoIt(), "DoSomethingEmptyGrain");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional")]
        public async Task DoSomethingGrainEmptyWithMoreTest()
        {
            IDoSomethingEmptyWithMoreGrain doSomething = GetHierarchyGrain<IDoSomethingEmptyWithMoreGrain>();
            Assert.AreEqual(await doSomething.DoIt(), "DoSomethingEmptyWithMoreGrain");
            Assert.AreEqual(await doSomething.DoMore(), "DoSomethingEmptyWithMoreGrain");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional")]
        public async Task DoSomethingWithMoreEmptyGrainTest()
        {
            IDoSomethingWithMoreEmptyGrain doSomething = GetHierarchyGrain<IDoSomethingWithMoreEmptyGrain>();
            Assert.AreEqual(await doSomething.DoIt(), "DoSomethingWithMoreEmptyGrain");
            Assert.AreEqual(await doSomething.DoMore(), "DoSomethingWithMoreEmptyGrain");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional")]
        public async Task DoSomethingWithMoreGrainTest()
        {
            IDoSomethingWithMoreGrain doSomething = GetHierarchyGrain<IDoSomethingWithMoreGrain>();
            Assert.AreEqual(await doSomething.DoIt(), "DoSomethingWithMoreGrain");
            Assert.AreEqual(await doSomething.DoThat(), "DoSomethingWithMoreGrain");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional")]
        public async Task DoSomethingCombinedGrainTest()
        {
            IDoSomethingCombinedGrain doSomething = GetHierarchyGrain<IDoSomethingCombinedGrain>();
            Assert.AreEqual(await doSomething.DoIt(), "DoSomethingCombinedGrain");
            Assert.AreEqual(await doSomething.DoMore(), "DoSomethingCombinedGrain");
            Assert.AreEqual(await doSomething.DoThat(), "DoSomethingCombinedGrain");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional")]
        public async Task DoSomethingValidateSingleGrainTest()
        {
            var doSomethingEmptyGrain = GetHierarchyGrain<IDoSomethingEmptyGrain>();
            var doSomethingEmptyWithMoreGrain = GetHierarchyGrain<IDoSomethingEmptyWithMoreGrain>();
            var doSomethingWithMoreEmptyGrain = GetHierarchyGrain<IDoSomethingWithMoreEmptyGrain>();
            var doSomethingWithMoreGrain = GetHierarchyGrain<IDoSomethingWithMoreGrain>();
            var doSomethingCombinedGrain = GetHierarchyGrain<IDoSomethingCombinedGrain>();

            await doSomethingEmptyGrain.SetA(10);
            await doSomethingEmptyWithMoreGrain.SetA(10);
            await doSomethingWithMoreEmptyGrain.SetA(10);
            await doSomethingWithMoreGrain.SetA(10);
            await doSomethingWithMoreGrain.SetB(10);
            await doSomethingCombinedGrain.SetA(10);
            await doSomethingCombinedGrain.SetB(10);
            await doSomethingCombinedGrain.SetC(10);

            await doSomethingEmptyGrain.IncrementA();
            await doSomethingEmptyWithMoreGrain.IncrementA();
            await doSomethingWithMoreEmptyGrain.IncrementA();
            await doSomethingWithMoreGrain.IncrementA();
            await doSomethingWithMoreGrain.IncrementB();
            await doSomethingCombinedGrain.IncrementA();
            await doSomethingCombinedGrain.IncrementB();
            await doSomethingCombinedGrain.IncrementC();

            Assert.AreEqual(await doSomethingEmptyGrain.GetA(), 11);
            Assert.AreEqual(await doSomethingEmptyWithMoreGrain.GetA(), 11);
            Assert.AreEqual(await doSomethingWithMoreEmptyGrain.GetA(), 11);
            Assert.AreEqual(await doSomethingWithMoreGrain.GetA(), 11);
            Assert.AreEqual(await doSomethingWithMoreGrain.GetB(), 11);
            Assert.AreEqual(await doSomethingCombinedGrain.GetA(), 11);
            Assert.AreEqual(await doSomethingCombinedGrain.GetB(), 11);
            Assert.AreEqual(await doSomethingCombinedGrain.GetC(), 11);

        }
    }
}
