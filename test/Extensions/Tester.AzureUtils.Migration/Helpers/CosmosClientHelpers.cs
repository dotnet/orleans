#if NET8_0_OR_GREATER
using System.Globalization;
using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
using Orleans;
using Orleans.Persistence.Cosmos;
using Orleans.Runtime;
using Tester.AzureUtils.Migration.Grains;
using TestExtensions;

namespace Tester.AzureUtils.Migration.Helpers
{
    internal static class CosmosClientHelpers
    {
        public static void ConfigureCosmosStorageOptions(this CosmosGrainStorageOptions options)
            => ConfigureCosmosStorageOptions(options, TestDefaultConfiguration.CosmosConnectionData);

        public static void ConfigureCosmosStorageOptions(this CosmosGrainStorageOptions options, CosmosConnection cosmosConnection)
        {
            if (!string.IsNullOrEmpty(cosmosConnection.ConnectionString))
            {
                options.ConfigureCosmosClient(connectionString: cosmosConnection.ConnectionString);
                return;
            }

            if (!string.IsNullOrEmpty(cosmosConnection.AccountEndpoint))
            {
                var azureCredentials = new DefaultAzureCredential();
                options.ConfigureCosmosClient(cosmosConnection.AccountEndpoint, tokenCredential: azureCredentials);
                return;
            }

            throw new InvalidOperationException("No valid Cosmos connection established.");
        }

        public static CosmosClient BuildClient()
            => BuildClient(TestDefaultConfiguration.CosmosConnectionData);

        public static CosmosClient BuildClient(CosmosConnection cosmosConnection)
        {
            if (!string.IsNullOrEmpty(cosmosConnection.ConnectionString))
            {
                return new CosmosClient(cosmosConnection.ConnectionString);
            }

            if (!string.IsNullOrEmpty(cosmosConnection.AccountEndpoint))
            {
                var azureCredentials = new DefaultAzureCredential();
                return new CosmosClient(cosmosConnection.AccountEndpoint, tokenCredential: azureCredentials);
            }

            throw new InvalidOperationException("No valid Cosmos connection established.");
        }

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