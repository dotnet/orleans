using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
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
        [TestCategory("BVT")]
        public async Task GetActivationAddressTest()
        {
            var mgmt1 = this.fixture.Client.GetGrain<IManagementGrain>(1);
            var mgmt2 = this.fixture.Client.GetGrain<IManagementGrain>(2);

            var mgmt1Address = await mgmt1.GetActivationAddress(mgmt1);
            Assert.NotNull(mgmt1Address);
            var mgmt2Address = await mgmt1.GetActivationAddress(mgmt2);
            Assert.Null(mgmt2Address);

            var mgmt1Address2 = await mgmt2.GetActivationAddress(mgmt1);
            Assert.NotNull(mgmt1Address2);
            Assert.True(mgmt1Address == mgmt1Address2);

            var worker = this.fixture.Client.GetGrain<IDumbWorker>(0);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(async () => await mgmt1.GetActivationAddress(worker));
        }
    }

    public interface IDumbWorker : IGrainWithIntegerKey
    {
        Task DoNothing();
    }

    [StatelessWorker(1)]
    public class DumbWorker : Grain, IDumbWorker
    {
        public Task DoNothing() => Task.CompletedTask;
    }
}