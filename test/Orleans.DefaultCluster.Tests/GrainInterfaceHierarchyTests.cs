using TestExtensions;
using TestGrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests
{
    /// <summary>
    /// Tests for Orleans' support of grain interface inheritance hierarchies.
    /// Validates that grains can properly implement interfaces that extend other interfaces,
    /// support multiple inheritance paths, and correctly expose methods from all levels
    /// of the interface hierarchy.
    /// </summary>
    public class GrainInterfaceHierarchyTests : HostedTestClusterEnsureDefaultStarted
    {
        public GrainInterfaceHierarchyTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        private T GetHierarchyGrain<T>() where T : IDoSomething, IGrainWithIntegerKey
        {
            return GrainFactory.GetGrain<T>(GetRandomGrainId());
        }

        /// <summary>
        /// Tests a grain implementing a simple interface hierarchy.
        /// Validates basic interface inheritance where a grain implements an interface
        /// that extends a base interface.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task DoSomethingGrainEmptyTest()
        {
            IDoSomethingEmptyGrain doSomething = GetHierarchyGrain<IDoSomethingEmptyGrain>();
            Assert.Equal("DoSomethingEmptyGrain", await doSomething.DoIt());
        }

        /// <summary>
        /// Tests a grain implementing an extended interface hierarchy.
        /// Validates that grains can implement interfaces that add additional methods
        /// to their base interface, exposing both sets of functionality.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task DoSomethingGrainEmptyWithMoreTest()
        {
            IDoSomethingEmptyWithMoreGrain doSomething = GetHierarchyGrain<IDoSomethingEmptyWithMoreGrain>();
            Assert.Equal("DoSomethingEmptyWithMoreGrain", await doSomething.DoIt());
            Assert.Equal("DoSomethingEmptyWithMoreGrain", await doSomething.DoMore());
        }

        /// <summary>
        /// Tests a grain implementing an interface with multiple method extensions.
        /// Validates proper method resolution when interfaces extend base interfaces
        /// with additional functionality.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task DoSomethingWithMoreEmptyGrainTest()
        {
            IDoSomethingWithMoreEmptyGrain doSomething = GetHierarchyGrain<IDoSomethingWithMoreEmptyGrain>();
            Assert.Equal("DoSomethingWithMoreEmptyGrain", await doSomething.DoIt());
            Assert.Equal("DoSomethingWithMoreEmptyGrain", await doSomething.DoMore());
        }

        /// <summary>
        /// Tests a grain with a different interface extension pattern.
        /// Validates that grains correctly implement alternative method sets
        /// when extending base interfaces.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task DoSomethingWithMoreGrainTest()
        {
            IDoSomethingWithMoreGrain doSomething = GetHierarchyGrain<IDoSomethingWithMoreGrain>();
            Assert.Equal("DoSomethingWithMoreGrain", await doSomething.DoIt());
            Assert.Equal("DoSomethingWithMoreGrain", await doSomething.DoThat());
        }

        /// <summary>
        /// Tests a grain implementing multiple interface inheritance paths.
        /// Validates that grains can implement complex interface hierarchies where
        /// multiple interfaces are combined, exposing all inherited methods.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task DoSomethingCombinedGrainTest()
        {
            IDoSomethingCombinedGrain doSomething = GetHierarchyGrain<IDoSomethingCombinedGrain>();
            Assert.Equal("DoSomethingCombinedGrain", await doSomething.DoIt());
            Assert.Equal("DoSomethingCombinedGrain", await doSomething.DoMore());
            Assert.Equal("DoSomethingCombinedGrain", await doSomething.DoThat());
        }

        /// <summary>
        /// Tests state management across different interface hierarchy implementations.
        /// Validates that grains with different interface hierarchies maintain independent
        /// state and that state operations work correctly through inherited interfaces.
        /// </summary>
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
