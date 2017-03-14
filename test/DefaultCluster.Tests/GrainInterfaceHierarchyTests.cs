using System.Threading.Tasks;
using Orleans;
using TestExtensions;
using TestGrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests
{
    public class GrainInterfaceHierarchyTests : HostedTestClusterEnsureDefaultStarted
    {
        public GrainInterfaceHierarchyTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        private T GetHierarchyGrain<T>() where T : IDoSomething, IGrainWithIntegerKey
        {
            return GrainFactory.GetGrain<T>(GetRandomGrainId());
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task DoSomethingGrainEmptyTest()
        {
            IDoSomethingEmptyGrain doSomething = GetHierarchyGrain<IDoSomethingEmptyGrain>();
            Assert.Equal(await doSomething.DoIt(), "DoSomethingEmptyGrain");
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task DoSomethingGrainEmptyWithMoreTest()
        {
            IDoSomethingEmptyWithMoreGrain doSomething = GetHierarchyGrain<IDoSomethingEmptyWithMoreGrain>();
            Assert.Equal(await doSomething.DoIt(), "DoSomethingEmptyWithMoreGrain");
            Assert.Equal(await doSomething.DoMore(), "DoSomethingEmptyWithMoreGrain");
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task DoSomethingWithMoreEmptyGrainTest()
        {
            IDoSomethingWithMoreEmptyGrain doSomething = GetHierarchyGrain<IDoSomethingWithMoreEmptyGrain>();
            Assert.Equal(await doSomething.DoIt(), "DoSomethingWithMoreEmptyGrain");
            Assert.Equal(await doSomething.DoMore(), "DoSomethingWithMoreEmptyGrain");
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task DoSomethingWithMoreGrainTest()
        {
            IDoSomethingWithMoreGrain doSomething = GetHierarchyGrain<IDoSomethingWithMoreGrain>();
            Assert.Equal(await doSomething.DoIt(), "DoSomethingWithMoreGrain");
            Assert.Equal(await doSomething.DoThat(), "DoSomethingWithMoreGrain");
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task DoSomethingCombinedGrainTest()
        {
            IDoSomethingCombinedGrain doSomething = GetHierarchyGrain<IDoSomethingCombinedGrain>();
            Assert.Equal(await doSomething.DoIt(), "DoSomethingCombinedGrain");
            Assert.Equal(await doSomething.DoMore(), "DoSomethingCombinedGrain");
            Assert.Equal(await doSomething.DoThat(), "DoSomethingCombinedGrain");
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
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

            Assert.Equal(await doSomethingEmptyGrain.GetA(), 11);
            Assert.Equal(await doSomethingEmptyWithMoreGrain.GetA(), 11);
            Assert.Equal(await doSomethingWithMoreEmptyGrain.GetA(), 11);
            Assert.Equal(await doSomethingWithMoreGrain.GetA(), 11);
            Assert.Equal(await doSomethingWithMoreGrain.GetB(), 11);
            Assert.Equal(await doSomethingCombinedGrain.GetA(), 11);
            Assert.Equal(await doSomethingCombinedGrain.GetB(), 11);
            Assert.Equal(await doSomethingCombinedGrain.GetC(), 11);

        }
    }
}
