using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos.Table;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Providers.Streams.PersistentStreams;
using Orleans.Serialization;
using Orleans.Streaming.AzureStorage;
using Orleans.Streams;
using TestExtensions;

namespace Tester.TestStreamProviders
{
    public class TestAzureTableStorageStreamFailureHandler : AzureTableStorageStreamFailureHandler<StreamDeliveryFailureEntity>
    {
        private const string TableName = "TestStreamFailures";
        private const string DeploymentId = "TestDeployment";
        private TestAzureTableStorageStreamFailureHandler(SerializationManager serializationManager)
            : base(serializationManager, NullLoggerFactory.Instance, false, DeploymentId, TableName, TestDefaultConfiguration.DataConnectionString)
        {
        }

        public static async Task<IStreamFailureHandler> Create(SerializationManager serializationManager)
        {
            var failureHandler = new TestAzureTableStorageStreamFailureHandler(serializationManager);
            await failureHandler.InitAsync();
            return failureHandler;
        }

        public static async Task<int> GetDeliveryFailureCount(string streamProviderName, ILoggerFactory loggerFactory)
        {
            var dataManager = new AzureTableDataManager<TableEntity>(
                TableName,
                TestDefaultConfiguration.DataConnectionString,
                loggerFactory.CreateLogger<AzureTableDataManager<TableEntity>>(),
                new AzureStoragePolicyOptions());
            await dataManager.InitTableAsync();
            IEnumerable<Tuple<TableEntity, string>> deliveryErrors =
                await
                    dataManager.ReadAllTableEntriesForPartitionAsync(
                        StreamDeliveryFailureEntity.MakeDefaultPartitionKey(streamProviderName, DeploymentId));
            return deliveryErrors.Count();
        }

        public static async Task DeleteAll()
        {
            var dataManager = new AzureTableDataManager<TableEntity>(
                TableName,
                TestDefaultConfiguration.DataConnectionString,
                NullLoggerFactory.Instance.CreateLogger<AzureTableDataManager<TableEntity>>(),
                new AzureStoragePolicyOptions());
            await dataManager.InitTableAsync();
            await dataManager.DeleteTableAsync();
        }
    }
}
