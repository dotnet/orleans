using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using Tester.AzureUtils.Migration.Grains;
using Tester.AzureUtils.Migration.Helpers;
using Xunit;

namespace Tester.AzureUtils.Migration.Abstractions
{
    public abstract class MigrationTableStorageToCosmosTestsWithBackgroundDataMigrator : MigrationBaseTests
    {
        const int baseId = 600;

        private readonly string _databaseName;
        private readonly string _containerName;

        private readonly CosmosClient _cosmosClient;
        private readonly TestCluster _hostedCluster;

        protected MigrationTableStorageToCosmosTestsWithBackgroundDataMigrator(BaseAzureTestClusterFixture fixture)
            : base(fixture)
        {
            _databaseName = MigrationAzureStorageTableToCosmosDbTests.OrleansDatabase;
            _containerName = MigrationAzureStorageTableToCosmosDbTests.OrleansContainer;

            _cosmosClient = CosmosClientHelpers.BuildClient();
            _hostedCluster = this.fixture.HostedCluster;
        }

        [SkippableFact]
        public async Task DataMigrator_MovesDataToDestinationStorage()
        {
            var grain = GetMigrationGrain(baseId + 1);
            var oldGrainState = new GrainState<MigrationTestGrain_State>(new() { A = 33, B = 806 });
            var stateName = typeof(MigrationTestGrain).FullName;

            await SourceStorage.WriteStateAsync(stateName, (GrainReference)grain, oldGrainState);

            // in tests hosted services are not started manually
            // therefore lets resolve cluster and start the hostedService in every silo (to simulate production scenario)
            if (_hostedCluster.Silos is { Count: > 0 })
            {
                foreach (var hostedSilo in _hostedCluster.Silos)
                {
                    if (hostedSilo is InProcessSiloHandle inProcessSilo)
                    {
                        var services = inProcessSilo.SiloHost.Services;
                        var hostedServices = services.GetServices<IHostedService>();
                        foreach (var hostedService in hostedServices)
                        {
                            _ = Task.Run(() => hostedService.StartAsync(CancellationToken.None));
                        }
                    }
                }
            }

            // it started, but we cant await it for sure. Lets add some artificial delay here
            // during this await DataMigrator should be started, and should already complete processing the grains
            // (only 1 inserted to source storage before)
            await Task.Delay(TimeSpan.FromSeconds(15));

            var cosmosGrainState = await GetGrainStateFromCosmosAsync(
                _cosmosClient,
                databaseName: _databaseName,
                containerName: _containerName,
                DocumentIdProvider,
                (GrainReference)grain);

            Assert.Equal(oldGrainState.State.A, cosmosGrainState.A);
            Assert.Equal(oldGrainState.State.B, cosmosGrainState.B);

            // rerun data migrator should not invoke anything -> all data is migrated already
            var statsRun2 = await DataMigrator.MigrateGrainsAsync(CancellationToken.None);
            Assert.True(statsRun2.SkippedAllEntries || statsRun2.SkippedEntries != 0); // it should skip entries (at least one - the one that we migrated on 1st DataMigrator.MigrateGrainsAsync() run)

            // ensure state one more time
            var cosmosGrainState2 = await GetGrainStateFromCosmosAsync(
                _cosmosClient,
                databaseName: _databaseName,
                containerName: _containerName,
                DocumentIdProvider,
                (GrainReference)grain);

            Assert.Equal(oldGrainState.State.A, cosmosGrainState2.A);
            Assert.Equal(oldGrainState.State.B, cosmosGrainState2.B);
        }
    }
}