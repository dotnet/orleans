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
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.ConfigureLegacyConfiguration(legacy =>
            {
                legacy.ClusterConfiguration.Globals.DefaultPlacementStrategy = "ActivationCountBasedPlacement";
                legacy.ClusterConfiguration.Globals.NumMissedProbesLimit = 1;
                legacy.ClusterConfiguration.Globals.NumVotesForDeathDeclaration = 1;
                legacy.ClusterConfiguration.Globals.TypeMapRefreshInterval = TimeSpan.FromMilliseconds(100);

                // use only Primary as the gateway
                legacy.ClientConfiguration.Gateways = legacy.ClientConfiguration.Gateways.Take(1).ToList();
            });
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
