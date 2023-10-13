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

        [Fact, TestCategory("BVT")]
        public async Task DoSomethingGrainEmptyTest()
        {
            IDoSomethingEmptyGrain doSomething = GetHierarchyGrain<IDoSomethingEmptyGrain>();
            Assert.Equal("DoSomethingEmptyGrain", await doSomething.DoIt());
        }

        [Fact, TestCategory("BVT")]
        public async Task DoSomethingGrainEmptyWithMoreTest()
        {
            IDoSomethingEmptyWithMoreGrain doSomething = GetHierarchyGrain<IDoSomethingEmptyWithMoreGrain>();
            Assert.Equal("DoSomethingEmptyWithMoreGrain", await doSomething.DoIt());
            Assert.Equal("DoSomethingEmptyWithMoreGrain", await doSomething.DoMore());
        }

        [Fact, TestCategory("BVT")]
        public async Task DoSomethingWithMoreEmptyGrainTest()
        {
            IDoSomethingWithMoreEmptyGrain doSomething = GetHierarchyGrain<IDoSomethingWithMoreEmptyGrain>();
            Assert.Equal("DoSomethingWithMoreEmptyGrain", await doSomething.DoIt());
            Assert.Equal("DoSomethingWithMoreEmptyGrain", await doSomething.DoMore());
        }

        [Fact, TestCategory("BVT")]
        public async Task DoSomethingWithMoreGrainTest()
        {
            IDoSomethingWithMoreGrain doSomething = GetHierarchyGrain<IDoSomethingWithMoreGrain>();
            Assert.Equal("DoSomethingWithMoreGrain", await doSomething.DoIt());
            Assert.Equal("DoSomethingWithMoreGrain", await doSomething.DoThat());
        }

        [Fact, TestCategory("BVT")]
        public async Task DoSomethingCombinedGrainTest()
        {
            IDoSomethingCombinedGrain doSomething = GetHierarchyGrain<IDoSomethingCombinedGrain>();
            Assert.Equal("DoSomethingCombinedGrain", await doSomething.DoIt());
            Assert.Equal("DoSomethingCombinedGrain", await doSomething.DoMore());
            Assert.Equal("DoSomethingCombinedGrain", await doSomething.DoThat());
        }

        [Fact, TestCategory("BVT")]
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

            Assert.Equal(11, await doSomethingEmptyGrain.GetA());
            Assert.Equal(11, await doSomethingEmptyWithMoreGrain.GetA());
            Assert.Equal(11, await doSomethingWithMoreEmptyGrain.GetA());
            Assert.Equal(11, await doSomethingWithMoreGrain.GetA());
            Assert.Equal(11, await doSomethingWithMoreGrain.GetB());
            Assert.Equal(11, await doSomethingCombinedGrain.GetA());
            Assert.Equal(11, await doSomethingCombinedGrain.GetB());
            Assert.Equal(11, await doSomethingCombinedGrain.GetC());

        }
    }
}
