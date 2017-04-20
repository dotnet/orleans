using System;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.MembershipTests
{
    public class SilosStopTests : TestClusterPerTest
    {
        public override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions(2);
            options.ClusterConfiguration.Globals.DefaultPlacementStrategy = "ActivationCountBasedPlacement";
            options.ClusterConfiguration.Globals.NumMissedProbesLimit = 1;
            options.ClusterConfiguration.Globals.NumVotesForDeathDeclaration = 1;
            options.ClusterConfiguration.Globals.TypeMapRefreshInterval = TimeSpan.FromMilliseconds(100);

            // use only Primary as the gateway
            options.ClientConfiguration.Gateways = options.ClientConfiguration.Gateways.Take(1).ToList();
            return new TestCluster(options);
        }

        [Fact, TestCategory("Functional"), TestCategory("Liveness")]
        public async Task SiloUngracefulShutdown_OutstandingRequestsBreak()
        {
            var grain = await GetGrainOnTargetSilo(HostedCluster.Primary);
            Assert.NotNull(grain);
            var target = await GetGrainOnTargetSilo(HostedCluster.SecondarySilos[0]);
            Assert.NotNull(target);

            var promise = grain.CallOtherLongRunningTask(target, true, TimeSpan.FromSeconds(7));

            await Task.Delay(500);
            HostedCluster.KillSilo(HostedCluster.SecondarySilos[0]);

            await Assert.ThrowsAsync<SiloUnavailableException>(() => promise);
        }

        [Fact, TestCategory("Functional"), TestCategory("Liveness")]
        public async Task SiloUngracefulShutdown_ClientOutstandingRequestsBreak()
        {
            var grain = GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
            var task = grain.LongRunningTask(true, TimeSpan.FromSeconds(7));
            await Task.Delay(500);

            HostedCluster.KillSilo(HostedCluster.SecondarySilos[0]);
            HostedCluster.KillSilo(HostedCluster.Primary);

            await Assert.ThrowsAsync<SiloUnavailableException>(() => task);
        }

        private async Task<ILongRunningTaskGrain<bool>> GetGrainOnTargetSilo(SiloHandle siloHandle)
        {
            const int maxRetry = 5;
            for (int i = 0; i < maxRetry; i++)
            {
                var grain = GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
                var instanceId = await grain.GetRuntimeInstanceId();
                if (instanceId.Contains(siloHandle.SiloAddress.Endpoint.ToString()))
                {
                    return grain;
                }
            }
            return null;
        }
    }
}
