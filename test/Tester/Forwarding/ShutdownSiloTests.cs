using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Orleans.TestingHost.Utils;
using Orleans.Hosting;
using Orleans.Configuration;
using System.Diagnostics;

namespace Tester.Forwarding
{
    public class ShutdownSiloTests : TestClusterPerTest
    {
        public const int NumberOfSilos = 2;

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            Assert.True(StorageEmulator.TryStart());
            builder.Options.InitialSilosCount = NumberOfSilos;
            builder.ConfigureLegacyConfiguration(legacy =>
            {
                legacy.ClusterConfiguration.Globals.DefaultPlacementStrategy = "ActivationCountBasedPlacement";
                legacy.ClusterConfiguration.Globals.NumMissedProbesLimit = 1;
                legacy.ClusterConfiguration.Globals.NumVotesForDeathDeclaration = 1;
            });
        }

        [Fact, TestCategory("Forward")]
        public async Task SiloGracefulShutdown_ForwardPendingRequest()
        {
            var grain = await GetLongRunningTaskGrainOnSecondary<bool>();

            // First call should be done on Secondary
            var promisesBeforeShutdown = grain.LongRunningTask(true, TimeSpan.FromSeconds(5));
            // Second call should be transfered to another silo
            var promisesAfterShutdown = grain.LongRunningTask(true, TimeSpan.FromSeconds(5));

            // Shutdown the silo where the grain is
            await Task.Delay(500);
            HostedCluster.StopSilo(HostedCluster.SecondarySilos.First());

            await promisesBeforeShutdown;
            await promisesAfterShutdown;
        }

        [Fact, TestCategory("GracefulShutdown"), TestCategory("Functional")]
        public async Task SiloGracefulShutdown_PendingRequestTimers()
        {
            var grain = await GetTimerRequestGrainOnSecondary();

            var promise = grain.StartAndWaitTimerTick(TimeSpan.FromSeconds(10));

            await Task.Delay(500);
            HostedCluster.StopSilo(HostedCluster.SecondarySilos.First());

            await promise;
        }

        [Fact, TestCategory("GracefulShutdown"), TestCategory("Functional")]
        public async Task SiloGracefulShutdown_StuckActivation()
        {
            var grain = await GetTimerRequestGrainOnSecondary();

            var promise = grain.StartAndWaitTimerTick(TimeSpan.FromMinutes(2));

            await Task.Delay(500);
            var stopwatch = Stopwatch.StartNew();
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            HostedCluster.SecondarySilos.First().StopSilo(true, cts.Token);
            stopwatch.Stop();
            Assert.True(stopwatch.Elapsed < TimeSpan.FromMinutes(1));
        }

        private async Task<ILongRunningTaskGrain<T>> GetLongRunningTaskGrainOnSecondary<T>()
        {
            while (true)
            {
                var grain = GrainFactory.GetGrain<ILongRunningTaskGrain<T>>(Guid.NewGuid());
                var instanceId = await grain.GetRuntimeInstanceId();
                if (instanceId.Contains(HostedCluster.SecondarySilos[0].SiloAddress.Endpoint.ToString()))
                {
                    return grain;
                }
            }
        }

        private async Task<ITimerRequestGrain> GetTimerRequestGrainOnSecondary()
        {
            var i = 0;
            while (true)
            {
                var grain = GrainFactory.GetGrain<ITimerRequestGrain>(i++);
                var instanceId = await grain.GetRuntimeInstanceId();
                if (instanceId.Contains(HostedCluster.SecondarySilos[0].SiloAddress.Endpoint.ToString()))
                {
                    return grain;
                }
            }
        }
    }
}
