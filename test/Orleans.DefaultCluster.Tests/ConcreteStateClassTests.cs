using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    /// <summary>
    /// Tests grain state persistence using concrete state classes.
    /// Verifies that grain state survives deactivation and reactivation cycles.
    /// Orleans supports automatic state persistence where grain state is:
    /// - Loaded when the grain activates
    /// - Saved when state changes (or on demand)
    /// - Preserved across grain deactivations
    /// This enables grains to maintain durable state without explicit database code.
    /// </summary>
    public class StateClassTests : HostedTestClusterEnsureDefaultStarted
    {
        private readonly Random rand = new Random();

        public StateClassTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        /// <summary>
        /// Tests basic state persistence functionality.
        /// Verifies that grain state persists across deactivation/reactivation cycles.
        /// The test:
        /// 1. Sets a value in grain state
        /// 2. Deactivates the grain
        /// 3. Reactivates the grain (gets new version/activation)
        /// 4. Verifies the state value was preserved
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task StateClassTests_StateClass()
        {
            await StateClassTests_Test("UnitTests.Grains.SimplePersistentGrain");
        }

        /// <summary>
        /// Helper method that performs the actual state persistence test.
        /// Demonstrates that:
        /// - Each grain activation gets a unique version identifier
        /// - State changes survive grain deactivation
        /// - New activations load the previously persisted state
        /// </summary>
        private async Task StateClassTests_Test(string grainClass)
        {
            var grain = this.GrainFactory.GetGrain<ISimplePersistentGrain>(GetRandomGrainId(), grainClass);
            var originalVersion = await grain.GetVersion();
            await grain.SetA(98, true); // deactivate grain after setting A

            var newVersion = await grain.GetVersion(); // get a new version from the new activation
            Assert.NotEqual(originalVersion, newVersion);
            var a = await grain.GetA();
            Assert.Equal(98, a); // value of A survive deactivation and reactivation of the grain
        }
    }
}
