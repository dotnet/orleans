#if NET8_0_OR_GREATER
using System.Globalization;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
using Orleans;
using Orleans.Runtime;
using Tester.AzureUtils.Migration.Grains;
using Tester.AzureUtils.Migration.Helpers;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils.Migration.Abstractions
{
    public abstract class MigrationTableStorageToCosmosTests : MigrationBaseTests
    {
        readonly CosmosClient _cosmosClient;

        protected MigrationTableStorageToCosmosTests(BaseAzureTestClusterFixture fixture)
            : base(fixture)
        {
            _cosmosClient = CosmosClientHelpers.BuildClient();
        }

        [Fact]
        public async Task ReadFromSourceTest()
        {
            var grain = this.fixture.Client.GetGrain<ISimplePersistentMigrationGrain>(100);
            var grainState = new GrainState<MigrationTestGrain_State>(new() { A = 33, B = 806 });
            var stateName = typeof(MigrationTestGrain).FullName;

            // Write directly to source storage
            await SourceStorage.WriteStateAsync(stateName, (GrainReference)grain, grainState);

            Assert.Equal(grainState.State.A, await grain.GetA());
            Assert.Equal(grainState.State.A * grainState.State.B, await grain.GetAxB());
        }

        [Fact]
        public async Task UpdatesStatesInBothStorages()
        {
            var grain = this.fixture.Client.GetGrain<ISimplePersistentMigrationGrain>(50000);
            var oldGrainState = new GrainState<MigrationTestGrain_State>(new() { A = 33, B = 806 });
            var newState = new MigrationTestGrain_State { A = 20, B = 30 };
            var stateName = typeof(MigrationTestGrain).FullName;

            // should write to both storages at this point
            await grain.SetA(33);
            await grain.SetB(806);

            // lets fetch data through cosmosClient
            var cosmosGrainState = await _cosmosClient.GetGrainStateFromCosmosAsync(DocumentIdProvider, stateName!, (GrainReference)grain);
            Assert.Equal(33, cosmosGrainState.A);
            Assert.Equal(806, cosmosGrainState.B);

            // and data in azure table storage should be in sync
            await SourceStorage.ReadStateAsync(stateName, (GrainReference)grain, oldGrainState);
            Assert.Equal(cosmosGrainState.A, oldGrainState.State.A);
            Assert.Equal(cosmosGrainState.B, oldGrainState.State.B);

            // update grain to a new state. Should happen in both storages again
            await grain.SetA(newState.A);
            await grain.SetB(newState.B);

            // verify updated state in both storages
            cosmosGrainState = await _cosmosClient.GetGrainStateFromCosmosAsync(DocumentIdProvider, stateName!, (GrainReference)grain);
            Assert.Equal(20, cosmosGrainState.A);
            Assert.Equal(30, cosmosGrainState.B);

            await SourceStorage.ReadStateAsync(stateName, (GrainReference)grain, oldGrainState);
            Assert.Equal(cosmosGrainState.A, oldGrainState.State.A);
            Assert.Equal(cosmosGrainState.B, oldGrainState.State.B);

            // lets make a final check - getting grain state via grain API should return same data
            Assert.Equal(cosmosGrainState.A, await grain.GetA());
            Assert.Equal(cosmosGrainState.A * cosmosGrainState.B, await grain.GetAxB());
        }

        [Fact]
        public async Task DataMigrator_MovesDataToDestinationStorage()
        {
            var grain = this.fixture.Client.GetGrain<ISimplePersistentMigrationGrain>(500);
            var oldGrainState = new GrainState<MigrationTestGrain_State>(new() { A = 33, B = 806 });
            var stateName = typeof(MigrationTestGrain).FullName;

            await SourceStorage.WriteStateAsync(stateName, (GrainReference)grain, oldGrainState);
            await DataMigrator.MigrateGrainsAsync(CancellationToken.None);

            var cosmosGrainState = await _cosmosClient.GetGrainStateFromCosmosAsync(DocumentIdProvider, stateName!, (GrainReference)grain);
            Assert.Equal(oldGrainState.State.A, cosmosGrainState.A);
            Assert.Equal(oldGrainState.State.B, cosmosGrainState.B);

            // rerun data migrator should not invoke anything -> all data is migrated already
            var statsRun2 = await DataMigrator.MigrateGrainsAsync(CancellationToken.None);
            Assert.True(statsRun2.SkippedEntries != 0); // it should skip entries (at least one - the one that we migrated on 1st DataMigrator.MigrateGrainsAsync() run)

            // ensure state one more time
            var cosmosGrainState2 = await _cosmosClient.GetGrainStateFromCosmosAsync(DocumentIdProvider, stateName!, (GrainReference)grain);
            Assert.Equal(oldGrainState.State.A, cosmosGrainState2.A);
            Assert.Equal(oldGrainState.State.B, cosmosGrainState2.B);
        }
    }
}
#endif