using System;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
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

            // use only Primary as the gateway
            options.ClientConfiguration.Gateways = options.ClientConfiguration.Gateways.Take(1).ToList();
            return new TestCluster(options);
        }

        [Fact, TestCategory("Functional"), TestCategory("Liveness")]
        public async Task SiloUngracefulShutdown_OutstandingRequestsBreak()
        {
            var grain = HostedCluster.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
            var instanceId = await grain.GetRuntimeInstanceId();
            var target = HostedCluster.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
            var targetInstanceId = await target.GetRuntimeInstanceId();

            var isOnSameSilo = instanceId == targetInstanceId;
            Assert.False(isOnSameSilo, "Activations must be placed on different silos");

            var promise = instanceId.Contains(HostedCluster.Primary.SiloAddress.Endpoint.ToString()) ?
                grain.CallOtherLongRunningTask(target, true, TimeSpan.FromSeconds(7))
                : target.CallOtherLongRunningTask(grain, true, TimeSpan.FromSeconds(7));

            await Task.Delay(500);
            HostedCluster.KillSilo(HostedCluster.SecondarySilos[0]);
            try
            {
                await promise;
                Assert.True(false, "The broken promise exception was not thrown");
            }
            catch (Exception ex)
            {
                Assert.Equal(typeof(SiloUnavailableException), ex.GetBaseException().GetType());
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Liveness")]
        public async Task SiloUngracefulShutdown_ClientOutstandingRequestsBreak()
        {
            var grain = HostedCluster.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
            var task = grain.LongRunningTask(true, TimeSpan.FromSeconds(7));
            await Task.Delay(500);

            HostedCluster.KillSilo(HostedCluster.SecondarySilos[0]);
            HostedCluster.KillSilo(HostedCluster.Primary);
            try
            {
                await task;
                Assert.True(false, "The broken promise exception was not thrown");
            }
            catch (Exception ex)
            {
                Assert.Equal(typeof(SiloUnavailableException), ex.GetBaseException().GetType());
            }
        }

    }
}
