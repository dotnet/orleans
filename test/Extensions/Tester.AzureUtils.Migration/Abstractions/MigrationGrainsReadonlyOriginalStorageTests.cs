#if NET8_0_OR_GREATER
using Orleans;
using Orleans.Runtime;
using Xunit;
using Tester.AzureUtils.Migration.Grains;
using Microsoft.Azure.Cosmos;
using TestExtensions;
using Tester.AzureUtils.Migration.Helpers;

namespace Tester.AzureUtils.Migration.Abstractions
{
    public abstract class MigrationGrainsReadonlyOriginalStorageTests : MigrationBaseTests
    {
        readonly string _databaseName;
        readonly string _containerName;

        readonly CosmosClient _cosmosClient;

        protected MigrationGrainsReadonlyOriginalStorageTests(BaseAzureTestClusterFixture fixture)
            : base(fixture)
        {
            _databaseName = MigrationReadonlyAzureStorageTableToCosmosDbTests.OrleansDatabase;
            _containerName = MigrationReadonlyAzureStorageTableToCosmosDbTests.OrleansContainer;

            _cosmosClient = CosmosClientHelpers.BuildClient();
        }

        [Fact]
        public async Task ReadFromSourceTest()
        {
            var grain = this.fixture.Client.GetGrain<ISimplePersistentMigrationGrain>(10);
            var grainState = new GrainState<MigrationTestGrain_State>(new() { A = 33, B = 806 });
            var stateName = typeof(MigrationTestGrain).FullName;

            // Write directly to source storage
            await SourceStorage.WriteStateAsync(stateName, (GrainReference)grain, grainState);

            Assert.Equal(grainState.State.A, await grain.GetA());
            Assert.Equal(grainState.State.A * grainState.State.B, await grain.GetAxB());
        }

        [Fact]
        public async Task UpdatesOnlyDestinationStorage()
        {
            var grain = this.fixture.Client.GetGrain<ISimplePersistentMigrationGrain>(11);
            var oldGrainState = new GrainState<MigrationTestGrain_State>(new() { A = 33, B = 806 });
            var newState = new MigrationTestGrain_State { A = 20, B = 30 };
            var stateName = typeof(MigrationTestGrain).FullName;

            await SourceStorage.WriteStateAsync(stateName, (GrainReference)grain, oldGrainState);

            // should write to only destination storage
            await grain.SetA(33);
            await grain.SetB(806);

            // lets fetch data through cosmosClient
            var cosmosGrainState = await GetGrainStateFromCosmosAsync(
                _cosmosClient,
                databaseName: _databaseName,
                containerName: _containerName, 
                DocumentIdProvider,
                (GrainReference)grain);

            Assert.Equal(33, cosmosGrainState.A);
            Assert.Equal(806, cosmosGrainState.B);

            // and data in azure table storage should be available (due to previous writeStateAsync call)
            await SourceStorage.ReadStateAsync(stateName, (GrainReference)grain, oldGrainState);
            Assert.Equal(cosmosGrainState.A, oldGrainState.State.A);
            Assert.Equal(cosmosGrainState.B, oldGrainState.State.B);

            // update grain to a new state. Should happen only in destination storage!
            await grain.SetA(newState.A);
            await grain.SetB(newState.B);

            // since saveMigrationMetadata is enabled, we should be able to check that metadata is already there
            Assert.NotNull(SourceExtendedStorage);
            var storageEntry = await SourceExtendedStorage!.GetStorageEntryAsync(stateName, (GrainReference)grain, oldGrainState);
            var migrationTime = await storageEntry.MigrationEntryClient.GetEntryMigrationTimeAsync();
            Assert.NotNull(migrationTime);

            // verify updated state only in destination storage
            cosmosGrainState = await GetGrainStateFromCosmosAsync(
                _cosmosClient,
                databaseName: _databaseName,
                containerName: _containerName,
                DocumentIdProvider,
                (GrainReference)grain);

            Assert.Equal(20, cosmosGrainState.A);
            Assert.Equal(30, cosmosGrainState.B);

            // old storage should not be updated
            await SourceStorage.ReadStateAsync(stateName, (GrainReference)grain, oldGrainState);
            Assert.Equal(33, oldGrainState.State.A);
            Assert.Equal(806, oldGrainState.State.B);

            // lets make a final check - getting grain state via grain API should return same data
            Assert.Equal(cosmosGrainState.A, await grain.GetA());
            Assert.Equal(cosmosGrainState.A * cosmosGrainState.B, await grain.GetAxB());
        }

        [Fact]
        public async Task DataMigrator_MovesDataToDestinationStorage()
        {
            var grain = this.fixture.Client.GetGrain<ISimplePersistentMigrationGrain>(12);
            var oldGrainState = new GrainState<MigrationTestGrain_State>(new() { A = 33, B = 806 });
            var stateName = typeof(MigrationTestGrain).FullName;

            await SourceStorage.WriteStateAsync(stateName, (GrainReference)grain, oldGrainState);

            await DataMigrator.MigrateGrainsAsync(CancellationToken.None);

            // ensure cosmos db state is updated
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
            Assert.True(statsRun2.SkippedEntries != 0); // it should skip entries (at least one - the one that we migrated on 1st DataMigrator.MigrateGrainsAsync() run)

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
#endif