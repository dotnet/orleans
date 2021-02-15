using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Azure.Identity;
using Microsoft.Azure.Cosmos.Table;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Persistence.AzureStorage;
using Orleans.Providers.Streams.PersistentStreams;
using TestExtensions;

namespace Tester.AzureUtils.Streaming
{
    public static class TestAzureTableStorageStreamFailureHandler
    {
        private const string TableName = "TestStreamFailures";
        private const string DeploymentId = "TestDeployment";

        public static async Task<int> GetDeliveryFailureCount(string streamProviderName)
        {
            var dataManager = GetDataManager();
            await dataManager.InitTableAsync();
            IEnumerable<Tuple<TableEntity, string>> deliveryErrors =
                await
                    dataManager.ReadAllTableEntriesForPartitionAsync(
                        StreamDeliveryFailureEntity.MakeDefaultPartitionKey(streamProviderName, DeploymentId));
            return deliveryErrors.Count();
        }

        public static async Task DeleteAll()
        {
            var dataManager = GetDataManager();
            await dataManager.InitTableAsync();
            await dataManager.DeleteTableAsync();
        }

        private static AzureTableDataManager<TableEntity> GetDataManager()
        {
            var options = new AzureStorageOperationOptions { TableName = TableName };
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                options.TableEndpoint = TestDefaultConfiguration.TableEndpoint;
                options.TableResourceId = TestDefaultConfiguration.TableResourceId;
                options.TokenCredential = new DefaultAzureCredential();
            }
            else
            {
                options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
            }
            return new AzureTableDataManager<TableEntity>(options, NullLogger.Instance);
        }
    }
}
