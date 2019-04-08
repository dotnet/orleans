using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans.Providers.Streams.PersistentStreams;
using Orleans.Serialization;
using Orleans.Streams;
using TestExtensions;
using Orleans.Persistence.AzureStorage;

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

        public static IStreamFailureHandler Create(IServiceProvider services, string name)
        {
            var serializationManager = services.GetService<SerializationManager>();
            return new TestAzureTableStorageStreamFailureHandler(serializationManager);
        }

        public static async Task<int> GetDeliveryFailureCount(string streamProviderName, ILoggerFactory loggerFactory)
        {
            var dataManager = new AzureTableDataManager<TableEntity>(TableName, TestDefaultConfiguration.DataConnectionString, loggerFactory);
            await dataManager.InitTableAsync();
            IEnumerable<Tuple<TableEntity, string>> deliveryErrors =
                await
                    dataManager.ReadAllTableEntriesForPartitionAsync(
                        StreamDeliveryFailureEntity.MakeDefaultPartitionKey(streamProviderName, DeploymentId));
            return deliveryErrors.Count();
        }

        public static async Task DeleteAll()
        {
            var dataManager = new AzureTableDataManager<TableEntity>(TableName, TestDefaultConfiguration.DataConnectionString, NullLoggerFactory.Instance);
            await dataManager.InitTableAsync();
            await dataManager.DeleteTableAsync();
        }
    }
}
