using Microsoft.Azure.Cosmos;
using Orleans.Reminders.Cosmos;
using TestExtensions;

namespace Tester.Reminders.Cosmos.Helpers
{
    internal static class CosmosClientHelpers
    {
        public static void ConfigureCosmosStorageOptions(this CosmosReminderTableOptions options)
        {
#if DEBUG
            options.ConfigureCosmosClient(accountEndpoint: TestDefaultConfiguration.CosmosDbEndpoint, tokenCredential: TestDefaultConfiguration.TokenCredential);
#else
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                options.ConfigureCosmosClient(accountEndpoint: TestDefaultConfiguration.CosmosDbEndpoint, tokenCredential: TestDefaultConfiguration.TokenCredential);
            }
            else if (!string.IsNullOrEmpty(TestDefaultConfiguration.CosmosDbConnectionString))
            {
                options.ConfigureCosmosClient(connectionString: TestDefaultConfiguration.CosmosDbConnectionString);
            }
            else
            {
                throw new ArgumentException($"CosmosDb connection is incorrectly configured. See {nameof(TestDefaultConfiguration.CosmosDbEndpoint)}", nameof(options));
            }
#endif
        }

        public static CosmosClient BuildClient()
        {
#if DEBUG
            return new CosmosClient(accountEndpoint: TestDefaultConfiguration.CosmosDbEndpoint, tokenCredential: TestDefaultConfiguration.TokenCredential);
#else
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                return new CosmosClient(accountEndpoint: TestDefaultConfiguration.CosmosDbEndpoint, tokenCredential: TestDefaultConfiguration.TokenCredential);
            }
            else if (!string.IsNullOrEmpty(TestDefaultConfiguration.CosmosDbConnectionString))
            {
                return new CosmosClient(connectionString: TestDefaultConfiguration.CosmosDbConnectionString);
            }

            throw new ArgumentException($"CosmosDb connection is incorrectly configured. See {nameof(TestDefaultConfiguration.CosmosDbEndpoint)}");
#endif
        }
    }
}
