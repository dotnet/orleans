﻿
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans.AzureUtils;
using Orleans.Providers.Streams.PersistentStreams;
using Orleans.Streams;
using Orleans.TestingHost;

namespace Tester.TestStreamProviders
{
    public class TestAzureTableStorageStreamFailureHandler : AzureTableStorageStreamFailureHandler<StreamDeliveryFailureEntity>
    {
        private const string TableName = "TestStreamFailures";
        private const string DeploymentId = "TestDeployment";

        private TestAzureTableStorageStreamFailureHandler()
            : base(false, DeploymentId, TableName, StorageTestConstants.DataConnectionString)
        {
        }

        public static async Task<IStreamFailureHandler> Create()
        {
            var failureHandler = new TestAzureTableStorageStreamFailureHandler();
            await failureHandler.InitAsync();
            return failureHandler;
        }

        public static async Task<int> GetDeliveryFailureCount(string streamProviderName)
        {
            var dataManager = new AzureTableDataManager<TableEntity>(TableName, StorageTestConstants.DataConnectionString);
            dataManager.InitTableAsync().Wait();
            IEnumerable<Tuple<TableEntity, string>> deliveryErrors =
                await
                    dataManager.ReadAllTableEntriesForPartitionAsync(
                        StreamDeliveryFailureEntity.MakeDefaultPartitionKey(streamProviderName, DeploymentId));
            return deliveryErrors.Count();
        }

        public static async Task DeleteAll()
        {
            var dataManager = new AzureTableDataManager<TableEntity>(TableName, StorageTestConstants.DataConnectionString);
            await dataManager.InitTableAsync();
            await dataManager.DeleteTableAsync();
        }
    }
}
