using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Orleans.Persistence.Cosmos;
using TestExtensions;

namespace Tester.AzureUtils.Migration.Helpers
{
    internal static class CosmosClientHelpers
    {
        public static void ConfigureCosmosStorageOptions(this CosmosGrainStorageOptions options)
        {
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                options.ConfigureCosmosClient(accountEndpoint: TestDefaultConfiguration.OrleansCosmosDbEndpoint, tokenCredential: TestDefaultConfiguration.TokenCredential);
            }
            else
            {
                options.ConfigureCosmosClient(connectionString: TestDefaultConfiguration.CosmosDbConnectionString);
            }

            throw new ArgumentNullException(nameof(options), "CosmosDb connection is not correctly configured");
        }

        public static CosmosClient BuildClient()
        {
            return TestDefaultConfiguration.UseAadAuthentication
                ? new CosmosClient(accountEndpoint: TestDefaultConfiguration.OrleansCosmosDbEndpoint, tokenCredential: TestDefaultConfiguration.TokenCredential)
                : new CosmosClient(connectionString: TestDefaultConfiguration.CosmosDbConnectionString);
        }
    }
}