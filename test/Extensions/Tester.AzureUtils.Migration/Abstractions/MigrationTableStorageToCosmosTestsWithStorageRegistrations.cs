using Microsoft.Azure.Cosmos;
using Orleans;
using Orleans.Runtime;
using Tester.AzureUtils.Migration.Grains;
using Tester.AzureUtils.Migration.Helpers;
using Xunit;

namespace Tester.AzureUtils.Migration.Abstractions
{
    public abstract class MigrationTableStorageToCosmosTestsWithStorageRegistrations : MigrationBaseTests
    {
        const int baseId = 700;

        public const string Migration1 = "migration1";
        public const string Migration2 = "migration2";
        public const string Source1 = "source1";
        public const string Source2 = "source2";
        public const string Destination1 = "destination1";
        public const string Destination2 = "destination2";

        readonly string _databaseName;
        readonly string _containerName;

        readonly CosmosClient _cosmosClient;

        protected MigrationTableStorageToCosmosTestsWithStorageRegistrations(BaseAzureTestClusterFixture fixture)
            : base(fixture)
        {
            _databaseName = MigrationAzureStorageTableToCosmosDbTestsWithStorageRegistrations.OrleansDatabase;
            _containerName = MigrationAzureStorageTableToCosmosDbTestsWithStorageRegistrations.OrleansContainer;

            _cosmosClient = CosmosClientHelpers.BuildClient();
        }

        [SkippableFact]
        public async Task ReadFromSourceTest()
        {
            var grain1 = GetMigrationGrain(baseId + 1, typeof(MigrationTestGrainStorage1));
            var grain2 = GetMigrationGrain(baseId + 2, typeof(MigrationTestGrainStorage2));
            var grainState1 = new GrainState<MigrationTestGrain_State>(new() { A = 33, B = 806 });
            var grainState2 = new GrainState<MigrationTestGrain_State>(new() { A = 33, B = 806 });

            var stateName1 = typeof(MigrationTestGrainStorage1).FullName;
            var stateName2 = typeof(MigrationTestGrainStorage2).FullName;

            // Write directly to source storage
            await GetStorage(Source1).WriteStateAsync(stateName1, (GrainReference)grain1, grainState1);
            Assert.Equal(grainState1.State.A, await grain1.GetA());
            Assert.Equal(grainState1.State.A * grainState1.State.B, await grain1.GetAxB());

            await GetStorage(Source2).WriteStateAsync(stateName2, (GrainReference)grain2, grainState2);
            Assert.Equal(grainState2.State.A, await grain2.GetA());
            Assert.Equal(grainState2.State.A * grainState2.State.B, await grain2.GetAxB());
        }

        [SkippableFact]
        public async Task UpdatesStatesInBothStorages_ForDifferentStorages()
        {
            var grain = GetMigrationGrain(baseId + 3, customType: typeof(MigrationTestGrainStorage1));
            var grain2 = GetMigrationGrain(baseId + 4, customType: typeof(MigrationTestGrainStorage2));
            var oldGrainState1 = new GrainState<MigrationTestGrain_State>(new() { A = 33, B = 806 });
            var oldGrainState2 = new GrainState<MigrationTestGrain_State>(new() { A = 33, B = 806 });
            var newState = new MigrationTestGrain_State { A = 20, B = 30 };
            var stateName = typeof(MigrationTestGrainStorage1).FullName;
            var stateName2 = typeof(MigrationTestGrainStorage2).FullName;

            // should write to both storages at this point
            await grain.SetA(33);
            await grain.SetB(806);

            await grain2.SetA(33);
            await grain2.SetB(806);

            // lets fetch data through cosmosClient
            var cosmosGrainState = await GetGrainStateFromCosmosAsync(
                _cosmosClient,
                databaseName: _databaseName,
                containerName: _containerName,
                DocumentIdProvider,
                (GrainReference)grain);

            Assert.Equal(33, cosmosGrainState.A);
            Assert.Equal(806, cosmosGrainState.B);

            var cosmosGrainState2 = await GetGrainStateFromCosmosAsync(
                _cosmosClient,
                databaseName: _databaseName,
                containerName: _containerName,
                DocumentIdProvider,
                (GrainReference)grain2);

            Assert.Equal(33, cosmosGrainState2.A);
            Assert.Equal(806, cosmosGrainState2.B);

            // and data in azure table storage should be in sync
            await GetStorage(Source1).ReadStateAsync(stateName, (GrainReference)grain, oldGrainState1);
            Assert.Equal(cosmosGrainState.A, oldGrainState1.State.A);
            Assert.Equal(cosmosGrainState.B, oldGrainState1.State.B);

            await GetStorage(Source2).ReadStateAsync(stateName2, (GrainReference)grain2, oldGrainState2);
            Assert.Equal(cosmosGrainState.A, oldGrainState2.State.A);
            Assert.Equal(cosmosGrainState.B, oldGrainState2.State.B);

            // update grain to a new state. Should happen in both storages again
            await grain.SetA(newState.A);
            await grain.SetB(newState.B);

            await grain2.SetA(newState.A);
            await grain2.SetB(newState.B);

            // verify updated state in both storages
            cosmosGrainState = await GetGrainStateFromCosmosAsync(
                _cosmosClient,
                databaseName: _databaseName,
                containerName: _containerName,
                DocumentIdProvider,
                (GrainReference)grain);

            Assert.Equal(20, cosmosGrainState.A);
            Assert.Equal(30, cosmosGrainState.B);

            cosmosGrainState2 = await GetGrainStateFromCosmosAsync(
                _cosmosClient,
                databaseName: _databaseName,
                containerName: _containerName,
                DocumentIdProvider,
                (GrainReference)grain2);

            Assert.Equal(20, cosmosGrainState.A);
            Assert.Equal(30, cosmosGrainState.B);

            await GetStorage(Source1).ReadStateAsync(stateName, (GrainReference)grain, oldGrainState1);
            Assert.Equal(cosmosGrainState.A, oldGrainState1.State.A);
            Assert.Equal(cosmosGrainState.B, oldGrainState1.State.B);

            await GetStorage(Source2).ReadStateAsync(stateName2, (GrainReference)grain2, oldGrainState2);
            Assert.Equal(cosmosGrainState2.A, oldGrainState2.State.A);
            Assert.Equal(cosmosGrainState2.B, oldGrainState2.State.B);

            // lets make a final check - getting grain state via grain API should return same data
            Assert.Equal(cosmosGrainState.A, await grain.GetA());
            Assert.Equal(cosmosGrainState.A * cosmosGrainState.B, await grain.GetAxB());

            Assert.Equal(cosmosGrainState2.A, await grain2.GetA());
            Assert.Equal(cosmosGrainState2.A * cosmosGrainState2.B, await grain2.GetAxB());
        }

        [SkippableFact]
        public async Task DataMigrator_MovesDataToDestinationStorage()
        {
            var grain1 = GetMigrationGrain(baseId + 5, typeof(MigrationTestGrainStorage1));
            var grain2 = GetMigrationGrain(baseId + 6, typeof(MigrationTestGrainStorage2));
            var oldGrainState1 = new GrainState<MigrationTestGrain_State>(new() { A = 33, B = 806 });
            var oldGrainState2 = new GrainState<MigrationTestGrain_State>(new() { A = 33, B = 806 });
            var stateName1 = typeof(MigrationTestGrain).FullName;
            var stateName2 = typeof(MigrationTestGrain).FullName;

            await GetStorage(Source1).WriteStateAsync(stateName1, (GrainReference)grain1, oldGrainState1);
            await GetStorage(Source2).WriteStateAsync(stateName2, (GrainReference)grain2, oldGrainState2);

            // we don't launch DataMigrator2 and we will ensure it did not run
            await GetDataMigrator(Migration1).MigrateGrainsAsync(CancellationToken.None);

            var cosmosGrainState1 = await GetGrainStateFromCosmosAsync(
                _cosmosClient,
                databaseName: _databaseName,
                containerName: _containerName,
                DocumentIdProvider,
                (GrainReference)grain1);

            Assert.Equal(oldGrainState1.State.A, cosmosGrainState1.A);
            Assert.Equal(oldGrainState1.State.B, cosmosGrainState1.B);

            try
            {
                _ = await GetGrainStateFromCosmosAsync(
                    _cosmosClient,
                    databaseName: _databaseName,
                    containerName: _containerName,
                    DocumentIdProvider,
                    (GrainReference)grain2);

                Assert.False(true, "DataMigrator2 should not run and therefore should not migrate data to Cosmos DB for grain2");
            }
            catch (Exception ex)
            {
                Assert.NotNull(ex);
            }

            // rerun data migrator should not invoke anything -> all data is migrated already
            var statsRun2 = await GetDataMigrator(Migration1).MigrateGrainsAsync(CancellationToken.None);
            Assert.True(statsRun2.SkippedAllEntries || statsRun2.SkippedEntries != 0); // it should skip entries (at least one - the one that we migrated on 1st DataMigrator.MigrateGrainsAsync() run)

            // ensure state one more time
            var cosmosGrainState11 = await GetGrainStateFromCosmosAsync(
                _cosmosClient,
                databaseName: _databaseName,
                containerName: _containerName,
                DocumentIdProvider,
                (GrainReference)grain1);

            Assert.Equal(oldGrainState1.State.A, cosmosGrainState11.A);
            Assert.Equal(oldGrainState1.State.B, cosmosGrainState11.B);
        }
    }
}