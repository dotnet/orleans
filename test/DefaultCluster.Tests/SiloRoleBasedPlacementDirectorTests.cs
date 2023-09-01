using Orleans.Runtime;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    public class SiloRoleBasedPlacementDirectorTests : HostedTestClusterEnsureDefaultStarted
    {
        public SiloRoleBasedPlacementDirectorTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        [Fact, TestCategory("Functional")]
        public async Task SiloRoleBasedPlacementDirector_CantFindSilo()
        {
            var grain = this.GrainFactory.GetGrain<ISiloRoleBasedPlacementGrain>("Sibyl.Silo");
            await Assert.ThrowsAsync<OrleansException>(() => grain.Ping());
        }

        [Fact, TestCategory("Functional")]
        public async Task SiloRoleBasedPlacementDirector_CanFindSilo()
        {
            var grain = this.GrainFactory.GetGrain<ISiloRoleBasedPlacementGrain>("testhost");
            bool result = await grain.Ping();
            Assert.True(result);
        }
    }
}
