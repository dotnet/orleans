using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Orleans.Persistence.Cosmos;
using TestExtensions;

namespace Tester.AzureUtils.Migration.Helpers
{
    internal static class CosmosClientHelpers
    {
        public static void ConfigureCosmosStorageOptions(this CosmosGrainStorageOptions options)
            => ConfigureCosmosStorageOptions(options, TestDefaultConfiguration.CosmosConnectionData);

        public static void ConfigureCosmosStorageOptions(this CosmosGrainStorageOptions options, CosmosConnection cosmosConnection)
        {
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                options.ConfigureCosmosClient(cosmosConnection.AccountEndpoint, tokenCredential: TestDefaultConfiguration.TokenCredential);
            }
            else
            {
                options.ConfigureCosmosClient(connectionString: cosmosConnection.ConnectionString);
            }
        }

        public static CosmosClient BuildClient()
            => BuildClient(TestDefaultConfiguration.CosmosConnectionData);

        public static CosmosClient BuildClient(CosmosConnection cosmosConnection)
        {
            return TestDefaultConfiguration.UseAadAuthentication
                ? new CosmosClient(cosmosConnection.AccountEndpoint, tokenCredential: TestDefaultConfiguration.TokenCredential)
                : new CosmosClient(cosmosConnection.ConnectionString);
        }
    }
}