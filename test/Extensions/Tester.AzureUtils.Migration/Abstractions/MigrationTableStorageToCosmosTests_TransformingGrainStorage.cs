#if NET8_0_OR_GREATER
using Microsoft.Azure.Cosmos;
using Orleans;
using Orleans.Runtime;
using Tester.AzureUtils.Migration.Grains;
using Tester.AzureUtils.Migration.Helpers;
using Xunit;

namespace Tester.AzureUtils.Migration.Abstractions
{
    public abstract class MigrationTableStorageToCosmosWithTransformGrainTests : MigrationBaseTests
    {
        const int baseId = 500; 

        readonly string _databaseName;
        readonly string _containerName;

        readonly CosmosClient _cosmosClient;

        protected MigrationTableStorageToCosmosWithTransformGrainTests(BaseAzureTestClusterFixture fixture)
            : base(fixture)
        {
            _databaseName = MigrationAzureStorageTableToCosmosDbTests.OrleansDatabase;
            _containerName = MigrationAzureStorageTableToCosmosDbTests.OrleansContainer;

            _cosmosClient = CosmosClientHelpers.BuildClient();
        }

        [Fact]
        public async Task UpdatesStatesInBothStorages_WithTransformedGrainType()
        {
            var grain = this.fixture.Client.GetGrain<ISimplePersistentMigrationGrain>(baseId + 2);
            var oldGrainState = new GrainState<MigrationTestGrain_State>(new() { A = 33, B = 806 });
            var newState = new MigrationTestGrain_State { A = 20, B = 30 };
            var stateName = typeof(MigrationTestGrain).FullName;

            // should write to both storages at this point
            await grain.SetA(33);
            await grain.SetB(806);

            // lets fetch data through cosmosClient
            var cosmosGrainState = await GetGrainStateJsonFromCosmosAsync(
                _cosmosClient,
                databaseName: _databaseName,
                containerName: _containerName,
                DocumentIdProvider,
                (GrainReference)grain);

            var customField = cosmosGrainState["MyCustomTest"];
            Assert.Equal(42, customField);
        }
    }
}
#endif