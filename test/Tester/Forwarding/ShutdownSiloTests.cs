using System;
using System.Collections.Generic;
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

        public static readonly TimeSpan DeactivationTimeout = TimeSpan.FromSeconds(10);

        internal class SiloBuilderConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder.Configure<GrainCollectionOptions>(options => options.DeactivationTimeout = DeactivationTimeout);
            }
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = NumberOfSilos;
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
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

            var tasks = new List<Task<string>>();
            for (int i = 0; i < 100; i++)
            {
                tasks.Add(grain.GetRuntimeInstanceIdWithDelay(TimeSpan.FromMilliseconds(50)));
            }

            // Shutdown the silo where the grain is
            await Task.Delay(500);
            await HostedCluster.StopSiloAsync(HostedCluster.SecondarySilos.First());

            var results = await Task.WhenAll(tasks);
            Assert.Equal(results[99], HostedCluster.Primary.SiloAddress.ToLongString());
        }

        [Fact, TestCategory("GracefulShutdown"), TestCategory("Functional")]
        public async Task SiloGracefulShutdown_PendingRequestTimers()
        {
            var grain = await GetTimerRequestGrainOnSecondary();

            var promise = grain.StartAndWaitTimerTick(TimeSpan.FromSeconds(10));

            await Task.Delay(500);
            await HostedCluster.StopSiloAsync(HostedCluster.SecondarySilos.First());

            await promise;
        }

        [Fact, TestCategory("GracefulShutdown"), TestCategory("Functional")]
        public async Task SiloGracefulShutdown_StuckTimers()
        {
            var grain = await GetTimerRequestGrainOnSecondary();

            await grain.StartStuckTimer(TimeSpan.Zero);

            await Task.Delay(TimeSpan.FromSeconds(1));
            var stopwatch = Stopwatch.StartNew();
            await HostedCluster.StopSiloAsync(HostedCluster.SecondarySilos.First());
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed > DeactivationTimeout);
        }

        [Fact, TestCategory("GracefulShutdown"), TestCategory("Functional")]
        public async Task SiloGracefulShutdown_StuckActivation()
        {
            var grain = await GetTimerRequestGrainOnSecondary();

            var promise = grain.StartAndWaitTimerTick(TimeSpan.FromMinutes(2));

            await Task.Delay(500);
            var stopwatch = Stopwatch.StartNew();
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await HostedCluster.SecondarySilos.First().StopSiloAsync(cts.Token);
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
