using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.OrleansRuntime
{
    public class ManagementGrainTests : OrleansTestingBase, IClassFixture<ManagementGrainTests.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.InitialSilosCount = 2;
            }
        }

        public ManagementGrainTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact]
        public async Task GetActivationAddressTest()
        {
            var mgmt1 = this.fixture.Client.GetGrain<IManagementGrain>(1);
            var mgmt2 = this.fixture.Client.GetGrain<IManagementGrain>(2);

            var mgmt1Address = await mgmt1.GetActivationAddress(mgmt1 as GrainReference);
            Assert.NotNull(mgmt1Address);
            var mgmt2Address = await mgmt1.GetActivationAddress(mgmt2 as GrainReference);
            Assert.Null(mgmt2Address);

            var mgmt1Address2 = await mgmt2.GetActivationAddress(mgmt1 as GrainReference);
            Assert.NotNull(mgmt1Address2);
            Assert.True(mgmt1Address == mgmt1Address2);
        }
    }
}