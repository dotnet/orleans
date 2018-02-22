using System;
using System.Linq;
using System.Threading.Tasks;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Orleans.TestingHost.Utils;
using Orleans.Hosting;
using Orleans.Configuration;

namespace Tester.Forwarding
{
    public class ShutdownSiloTests : TestClusterPerTest
    {
        public const int NumberOfSilos = 2;

        private class SiloBuilderConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder.AddAzureBlobGrainStorage("MemoryStore", (AzureBlobStorageOptions options) =>
                {
                    options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                });
            }
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            Assert.True(StorageEmulator.TryStart());
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
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
    }
}
