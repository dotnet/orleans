using Orleans.Concurrency;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace UnitTests.OrleansRuntime
{
    /// <summary>
    /// Tests for the Orleans Management Grain functionality.
    /// The Management Grain is a system grain that provides cluster management operations
    /// such as querying grain activation information, collecting statistics, and
    /// performing administrative tasks across the Orleans cluster.
    /// </summary>
    public class ManagementGrainTests : HostedTestClusterEnsureDefaultStarted
    {
        public ManagementGrainTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        /// <summary>
        /// Tests querying grain activation addresses through the management grain.
        /// Verifies that:
        /// - Non-activated grains return null addresses
        /// - Activated grains return valid addresses that remain stable
        /// - Stateless worker grains throw appropriate exceptions (no single activation)
        /// This functionality is crucial for diagnostics and monitoring grain placement.
        /// </summary>
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

    /// <summary>
    /// Test interface for a stateless worker grain.
    /// Used to verify that management operations handle stateless workers correctly.
    /// </summary>
    public interface IDumbWorker : IGrainWithIntegerKey
    {
        Task DoNothing();
    }

    /// <summary>
    /// Test implementation of a stateless worker grain.
    /// Stateless workers can have multiple activations and don't have a single address,
    /// which makes them special cases for management operations.
    /// </summary>
    [StatelessWorker(1)]
    public class DumbWorker : Grain, IDumbWorker
    {
        public Task DoNothing() => Task.CompletedTask;
    }

    /// <summary>
    /// Test interface for a regular stateful grain.
    /// Used to verify normal grain activation address queries.
    /// </summary>
    public interface IDumbGrain : IGrainWithGuidKey
    {
        Task DoNothing();
    }

    /// <summary>
    /// Test implementation of a regular stateful grain.
    /// Regular grains have a single activation at any time,
    /// making their addresses queryable through management operations.
    /// </summary>
    public class DumbGrain : Grain, IDumbGrain
    {
        public Task DoNothing() => Task.CompletedTask;
    }
}