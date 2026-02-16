using Azure.Data.Tables;
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
            var deliveryErrors =
                await dataManager.ReadAllTableEntriesForPartitionAsync(
                        StreamDeliveryFailureEntity.MakeDefaultPartitionKey(streamProviderName, DeploymentId));
            return deliveryErrors.Count;
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
            options.TableServiceClient = new(AzuriteContainerManager.ConnectionString);
            return new AzureTableDataManager<TableEntity>(options, NullLogger.Instance);
        }
    }
}
