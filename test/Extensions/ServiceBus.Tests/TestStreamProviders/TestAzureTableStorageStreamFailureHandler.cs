using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Azure.Identity;
using Microsoft.Azure.Cosmos.Table;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Providers.Streams.PersistentStreams;
using Orleans.Serialization;
using Orleans.Streaming.EventHubs;
using Orleans.Streams;
using TestExtensions;

namespace ServiceBus.Tests.TestStreamProviders.EventHub
{
    public class TestAzureTableStorageStreamFailureHandler : AzureTableStorageStreamFailureHandler<StreamDeliveryFailureEntity>
    {
        private const string TableName = "TestStreamFailures";
        private const string DeploymentId = "TestDeployment";
        private TestAzureTableStorageStreamFailureHandler(Serializer<StreamSequenceToken> serializer)
            : base(serializer, NullLoggerFactory.Instance, false, DeploymentId, TableName, TestDefaultConfiguration.DataConnectionString)
        {
        }

        public static async Task<IStreamFailureHandler> Create(Serializer<StreamSequenceToken> serializer)
        {
            var failureHandler = new TestAzureTableStorageStreamFailureHandler(serializer);
            await failureHandler.InitAsync();
            return failureHandler;
        }

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
