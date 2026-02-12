using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    /// <summary>
    /// Tests grain deactivation and reactivation behaviors in Orleans.
    /// Grain deactivation is a key part of Orleans' resource management:
    /// - Grains can be explicitly deactivated or automatically deactivated when idle
    /// - Deactivation releases memory and other resources
    /// - Reactivation occurs transparently when a deactivated grain is called again
    /// - State is preserved across deactivation/reactivation cycles
    /// These tests verify the performance and correctness of this mechanism.
    /// </summary>
    public class DeactivationTests : HostedTestClusterEnsureDefaultStarted
    {
        public DeactivationTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        /// <summary>
        /// Tests the timing of grain deactivation and reactivation.
        /// Verifies that:
        /// - Explicit deactivation followed by reactivation completes quickly (under 1 second)
        /// - The grain gets a new activation (new version) after deactivation
        /// - State is preserved across the deactivation/reactivation cycle
        /// This ensures Orleans can efficiently manage grain lifecycle without significant overhead.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task DeactivateReactivateTiming()
        {
            var x = GetRandomGrainId();
            var grain = this.GrainFactory.GetGrain<ISimplePersistentGrain>(x);
            var originalVersion = await grain.GetVersion();

            var sw = Stopwatch.StartNew();

            await grain.SetA(99, true); // deactivate grain after setting A value
            var newVersion = await grain.GetVersion(); // get a new version from the new activation
            Assert.NotEqual(originalVersion, newVersion);

            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 1000);
            this.Logger.LogInformation("Took {ElapsedMilliseconds}ms to deactivate and reactivate the grain", sw.ElapsedMilliseconds);

            var a = await grain.GetA();
            Assert.Equal(99, a); // value of A survive deactivation and reactivation of the grain
        }
    }
}
