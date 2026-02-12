using Orleans.Runtime;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    /// <summary>
    /// Tests for silo role-based placement strategy in Orleans.
    /// This placement strategy allows grains to be placed on specific silos
    /// based on silo roles/names. This is useful for scenarios requiring
    /// grain affinity to specific infrastructure (e.g., grains that need
    /// access to local resources or should run on specialized hardware).
    /// </summary>
    public class SiloRoleBasedPlacementDirectorTests : HostedTestClusterEnsureDefaultStarted
    {
        public SiloRoleBasedPlacementDirectorTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        /// <summary>
        /// Tests placement failure when the requested silo role doesn't exist.
        /// Verifies that attempting to place a grain on a non-existent silo role
        /// throws an appropriate exception, ensuring the system fails fast
        /// when misconfigured rather than silently placing grains incorrectly.
        /// </summary>
        [Fact, TestCategory("Functional")]
        public async Task SiloRoleBasedPlacementDirector_CantFindSilo()
        {
            var grain = this.GrainFactory.GetGrain<ISiloRoleBasedPlacementGrain>("Sibyl.Silo");
            await Assert.ThrowsAsync<OrleansException>(() => grain.Ping());
        }

        /// <summary>
        /// Tests successful placement on a silo with the requested role.
        /// Verifies that grains configured for role-based placement are
        /// correctly activated on silos matching the specified role,
        /// demonstrating the basic functionality of targeted placement.
        /// </summary>
        [Fact, TestCategory("Functional")]
        public async Task SiloRoleBasedPlacementDirector_CanFindSilo()
        {
            var grain = this.GrainFactory.GetGrain<ISiloRoleBasedPlacementGrain>("testhost");
            bool result = await grain.Ping();
            Assert.True(result);
        }
    }
}
