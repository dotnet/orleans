#if NET8_0_OR_GREATER
using System.Globalization;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
using Orleans;
using Orleans.Persistence.Cosmos;
using Orleans.Runtime;
using Tester.AzureUtils.Migration.Grains;

namespace Tester.AzureUtils.Migration.Helpers
{
    internal static class CosmosClientHelpers
    {
        /// <summary>
        /// Loads currently stored grain state from Cosmos DB.
        /// </summary>
        /// <remarks>
        /// We can't call `DestinationStorage.ReadAsync()` because of the inner implementation details
        /// </remarks>
        public static async Task<MigrationTestGrain_State> GetGrainStateFromCosmosAsync(
            this CosmosClient cosmosClient,
            IDocumentIdProvider documentIdProvider,
            string stateName,
            GrainReference grain)
        {
            var database = cosmosClient.GetDatabase(MigrationAzureStorageTableToCosmosDbTests.OrleansDatabase);
            var container = database.Client.GetContainer(database.Id, MigrationAzureStorageTableToCosmosDbTests.OrleansContainer);

            var grainId = grain.GetPrimaryKeyLong();
            var grainIdRepresentation = grainId.ToString("X", CultureInfo.InvariantCulture); // document number is represented in Cosmos in such a way
            var (documentId, partitionKey) = documentIdProvider.GetDocumentIdentifiers(
                stateName,
                "migrationtestgrain", // GrainTypeAttribute's value for MigrationTestGrain
                grainIdRepresentation);
            var response = await container.ReadItemAsync<dynamic>(documentId, new PartitionKey(partitionKey));
            JObject data = response.Resource;
            var dataState = data["state"]!;

            return new MigrationTestGrain_State
            {
                A = dataState["A"]!.Value<int>(),
                B = dataState["B"]!.Value<int>()
            };
        }
    }
}
#endif