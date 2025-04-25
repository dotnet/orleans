using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Orleans.Persistence.Cosmos;
using Orleans.Reminders.Cosmos.Migration;
using TestExtensions;

namespace Tester.Reminders.Cosmos.Migration.Helpers
{
    internal static class CosmosClientHelpers
    {
        public static void ConfigureCosmosStorageOptions(this CosmosReminderTableOptions options)
        {
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
        }

        public static CosmosClient BuildClient()
        {
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                return new CosmosClient(accountEndpoint: TestDefaultConfiguration.CosmosDbEndpoint, tokenCredential: TestDefaultConfiguration.TokenCredential);
            }
            else if (!string.IsNullOrEmpty(TestDefaultConfiguration.CosmosDbConnectionString))
            {
                return new CosmosClient(connectionString: TestDefaultConfiguration.CosmosDbConnectionString);
            }

            throw new ArgumentException($"CosmosDb connection is incorrectly configured. See {nameof(TestDefaultConfiguration.CosmosDbEndpoint)}");
        }
    }
}
