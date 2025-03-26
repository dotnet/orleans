using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
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
                throw new Exception($"DEBUG for AAD: OrleansCosmosDbEndpoint='{TestDefaultConfiguration.OrleansCosmosDbEndpoint}'");
                // options.ConfigureCosmosClient(accountEndpoint: TestDefaultConfiguration.OrleansCosmosDbEndpoint, tokenCredential: TestDefaultConfiguration.TokenCredential);
            }
            else if (!string.IsNullOrEmpty(TestDefaultConfiguration.CosmosDbConnectionString))
            {
                options.ConfigureCosmosClient(connectionString: TestDefaultConfiguration.CosmosDbConnectionString);
            }
            else
            {
                throw new ArgumentException($"CosmosDb connection is incorrectly configured. See {nameof(TestDefaultConfiguration.OrleansCosmosDbEndpoint)}", nameof(options));
            }
        }

        public static CosmosClient BuildClient()
        {
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                return new CosmosClient(accountEndpoint: TestDefaultConfiguration.OrleansCosmosDbEndpoint, tokenCredential: TestDefaultConfiguration.TokenCredential);
            }
            else if (!string.IsNullOrEmpty(TestDefaultConfiguration.CosmosDbConnectionString))
            {
                return new CosmosClient(connectionString: TestDefaultConfiguration.CosmosDbConnectionString);
            }

            throw new ArgumentException($"CosmosDb connection is incorrectly configured. See {nameof(TestDefaultConfiguration.OrleansCosmosDbEndpoint)}");
        }
    }
}