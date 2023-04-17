using Orleans.Concurrency;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace UnitTests.OrleansRuntime
{
    public class ManagementGrainTests : HostedTestClusterEnsureDefaultStarted
    {
        public ManagementGrainTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        [Fact]
        [TestCategory("BVT")]
        public async Task GetActivationAddressTest()
        {
            var managementGrain = this.Fixture.Client.GetGrain<IManagementGrain>(0);
            var grain1 = this.Fixture.Client.GetGrain<IDumbGrain>(Guid.NewGuid());
            var grain2 = this.Fixture.Client.GetGrain<IDumbGrain>(Guid.NewGuid());

            var grain1Address = await managementGrain.GetActivationAddress(grain1);
            Assert.Null(grain1Address);

            await grain1.DoNothing();
            grain1Address = await managementGrain.GetActivationAddress(grain1);
            Assert.NotNull(grain1Address);
            var grain2Address = await managementGrain.GetActivationAddress(grain2);
            Assert.Null(grain2Address);

            await grain2.DoNothing();
            var grain1Address2 = await managementGrain.GetActivationAddress(grain1);
            Assert.NotNull(grain1Address2);
            Assert.True(grain1Address.Equals(grain1Address2));

            var worker = this.Fixture.Client.GetGrain<IDumbWorker>(0);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(async () => await managementGrain.GetActivationAddress(worker));
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

    public interface IDumbGrain : IGrainWithGuidKey
    {
        Task DoNothing();
    }

    public class DumbGrain : Grain, IDumbGrain
    {
        public Task DoNothing() => Task.CompletedTask;
    }
}