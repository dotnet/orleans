using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Azure.Identity;
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
                new AzureStorageOperationOptions { TableName = TableName }.ConfigureTestDefaults(),
                loggerFactory.CreateLogger<AzureTableDataManager<TableEntity>>());
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
                new AzureStorageOperationOptions { TableName = TableName }.ConfigureTestDefaults(),
                NullLoggerFactory.Instance.CreateLogger<AzureTableDataManager<TableEntity>>());
            await dataManager.InitTableAsync();
            await dataManager.DeleteTableAsync();
        }
    }

    internal static class AzureStorageOperationOptionsExtensions
    {
        public static AzureStorageOperationOptions ConfigureTestDefaults(this AzureStorageOperationOptions options)
        {
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

            return options;
        }
    }
}
