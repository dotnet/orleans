using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Orleans.Configuration;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Azure.Data.Tables;
using Azure.Identity;
using Orleans.Runtime.Placement;

namespace Tester.Forwarding
{
    /// <summary>
    /// Tests for silo shutdown scenarios including request forwarding, timer handling, and stuck activations.
    /// </summary>
    public class ShutdownSiloTests : TestClusterPerTest
    {
        public const int NumberOfSilos = 2;

        public static readonly TimeSpan DeactivationTimeout = TimeSpan.FromSeconds(10);
        internal class SiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .Configure<GrainCollectionOptions>(options =>
                    {
                        options.DeactivationTimeout = DeactivationTimeout;
                    })
                    .UseAzureStorageClustering(options => options.TableServiceClient = GetTableServiceClient())
                    .ConfigureServices(services => services.AddSingleton<PlacementStrategy, ActivationCountBasedPlacement>())
                    .Configure<ClusterMembershipOptions>(options =>
                    {
                        options.NumMissedProbesLimit = 1;
                        options.NumVotesForDeathDeclaration = 1;
                    });
            }

            private static TableServiceClient GetTableServiceClient()
            {
                return TestDefaultConfiguration.UseAadAuthentication
                    ? new(TestDefaultConfiguration.TableEndpoint, TestDefaultConfiguration.TokenCredential)
                    : new(TestDefaultConfiguration.DataConnectionString);
            }
        }

        protected override void CheckPreconditionsOrThrow()
        {
            base.CheckPreconditionsOrThrow();
            TestUtils.CheckForAzureStorage();
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = NumberOfSilos;
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
        }

        public ShutdownSiloTests()
        {
            this.EnsurePreconditionsMet();
        }

        [Fact(Skip = "https://github.com/dotnet/orleans/issues/6423"), TestCategory("Forward"), TestCategory("Functional")]
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
            Assert.Equal(results[99], HostedCluster.Primary.SiloAddress.ToString());
        }

        [SkippableFact, TestCategory("GracefulShutdown"), TestCategory("Functional")]
        public async Task SiloGracefulShutdown_PendingRequestTimers()
        {
            var grain = await GetTimerRequestGrainOnSecondary();

            var promise = grain.StartAndWaitTimerTick(TimeSpan.FromSeconds(10));

            await Task.Delay(500);
            await HostedCluster.StopSiloAsync(HostedCluster.SecondarySilos.First());

            await promise;
        }

        [SkippableFact, TestCategory("GracefulShutdown"), TestCategory("Functional")]
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

        [SkippableFact, TestCategory("GracefulShutdown"), TestCategory("Functional")]
        public async Task SiloGracefulShutdown_StuckActivation()
        {
            var grain = await GetTimerRequestGrainOnSecondary();
            _ = grain.StartAndWaitTimerTick(TimeSpan.FromMinutes(2));

            await Task.Delay(500);
            var stopwatch = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await HostedCluster.SecondarySilos.First().StopSiloAsync(cts.Token);
            stopwatch.Stop();
            Assert.True(stopwatch.Elapsed < TimeSpan.FromMinutes(1));
        }

        private async Task<ILongRunningTaskGrain<T>> GetLongRunningTaskGrainOnSecondary<T>()
        {
            while (true)
            {
                RequestContext.Set(IPlacementDirector.PlacementHintKey, HostedCluster.SecondarySilos[0].SiloAddress);
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
                RequestContext.Set(IPlacementDirector.PlacementHintKey, HostedCluster.SecondarySilos[0].SiloAddress);
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
