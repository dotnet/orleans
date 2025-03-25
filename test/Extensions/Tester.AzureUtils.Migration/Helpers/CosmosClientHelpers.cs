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
    }
}