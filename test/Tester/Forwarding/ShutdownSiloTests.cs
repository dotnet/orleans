using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Xunit;

namespace Tester.Forwarding
{
    public class ShutdownSiloTests : TestClusterPerTest
    {
        public const int NumberOfSilos = 2;

        public override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions(NumberOfSilos);
            options.ClientConfiguration.PreferedGatewayIndex = 1; // Not on primary
            options.ClusterConfiguration.Globals.DefaultPlacementStrategy = "ActivationCountBasedPlacement";
            options.ClusterConfiguration.Globals.NumMissedProbesLimit = 1;
            options.ClusterConfiguration.Globals.NumVotesForDeathDeclaration = 1;
            return new TestCluster(options);
        }

        [Fact, TestCategory("Functional"), TestCategory("Forward")]
        public async Task SiloGracefulShutdown_NoFailureOnGatewayStop()
        {
            var grain = await GetLongRunningTaskGrainOnPrimary<bool>();

            // Put some work on a grain which is on Primary silo
            var promises = new List<Task>
            {
                grain.LongRunningTask(true, TimeSpan.FromSeconds(5)),
                grain.LongRunningTask(true, TimeSpan.FromSeconds(5))
            };

            // Shutdown the silo where the gateway we use is
            await Task.Delay(500);
            HostedCluster.StopSilo(HostedCluster.SecondarySilos.First());

            // This call should raise SiloUnavailableException because the gateway is closed
            await Assert.ThrowsAsync<SiloUnavailableException>(grain.GetRuntimeInstanceId);

            // Should not raise any exception because response should come from another silo
            await Task.WhenAll(promises);
        }

        private async Task<ILongRunningTaskGrain<T>> GetLongRunningTaskGrainOnPrimary<T>()
        {
            while (true)
            {
                var grain = HostedCluster.GrainFactory.GetGrain<ILongRunningTaskGrain<T>>(Guid.NewGuid());
                var instanceId = await grain.GetRuntimeInstanceId();
                if (instanceId.Contains(HostedCluster.Primary.SiloAddress.Endpoint.ToString()))
                {
                    return grain;
                }
            }
        }
    }
}
