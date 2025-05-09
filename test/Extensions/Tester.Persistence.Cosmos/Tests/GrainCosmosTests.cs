using Microsoft.Azure.Cosmos;
using Orleans;
using Orleans.Providers;
using Orleans.Runtime;
using Tester.AzureUtils.Migration;
using Tester.AzureUtils.Migration.Abstractions;
using Tester.AzureUtils.Migration.Grains;
using Tester.AzureUtils.Migration.Helpers;
using Xunit;

namespace Tester.Persistence.Cosmos.Tests;

public abstract class GrainCosmosTests : MigrationBaseTests
{
    const int baseId = 2000;

    readonly string _databaseName;
    readonly string _containerName;

    readonly CosmosClient _cosmosClient;

    protected GrainCosmosTests(BaseAzureTestClusterFixture fixture)
        : base(fixture)
    {
        _databaseName = Resources.MigrationDatabase;
        _containerName = Resources.MigrationLatestContainer;

        _cosmosClient = CosmosClientHelpers.BuildClient();
    }

    [SkippableFact]
    public async Task ReadWriteSequence_AlwaysSuccessfullyCommunicatesWithCosmos()
    {
        var grain = GetMigrationGrain(baseId + 2);
        var oldGrainState = new GrainState<MigrationTestGrain_State>(new() { A = 33, B = 806 });
        var newState = new MigrationTestGrain_State { A = 20, B = 30 };
        var stateName = typeof(MigrationTestGrain).FullName;

        // should write to both storages at this point
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

        // and data in azure table storage should be in sync
        await GetStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME).ReadStateAsync(stateName, (GrainReference)grain, oldGrainState);
        Assert.Equal(cosmosGrainState.A, oldGrainState.State.A);
        Assert.Equal(cosmosGrainState.B, oldGrainState.State.B);
    }
}