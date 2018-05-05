using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans.Configuration;
using Orleans.Configuration.Development;
using Orleans.Runtime.Configuration;
using Orleans.Transactions.Abstractions;
using TestExtensions;
using Xunit;
using Orleans.Persistence.AzureStorage;

namespace Orleans.Transactions.AzureStorage.Tests
{
    [TestCategory("Azure"), TestCategory("Transactions"), TestCategory("BVT")]
    public class AzureTransactionLogTests
    {
        private static string ClusterServiceId = Guid.NewGuid().ToString();
        public AzureTransactionLogTests()
        {
            TestFixture.CheckForAzureStorage(TestDefaultConfiguration.DataConnectionString);
        }

        [SkippableFact]
        public async Task TransactionLogCanArchiveAndQuery()
        {
            var azureOptions = new AzureTransactionLogOptions()
            {
                ConnectionString = TestDefaultConfiguration.DataConnectionString,
                TableName = "TransactionLog"
            };
            var archiveOptions = new AzureTransactionArchiveLogOptions()
            {
                ArchiveLog = true
            };

            var logStorage = await StorageFactory(azureOptions, archiveOptions);
            var recordsNum = 20;
            var allTransactions = new List<CommitRecord>(recordsNum);
            for (int i = 0; i< recordsNum; i++)
            {
                allTransactions.Add(new CommitRecord(){LSN=i, TransactionId = i});
            }
            await logStorage.Append(allTransactions);
            //all transactions will be archived 
            var maxSLN = recordsNum - 11;
            await logStorage.TruncateLog(maxSLN);
            //get all archived records
            var archQuery = new TableQuery<AzureTransactionLogStorage.ArchivalRow>().Where(TableQuery.CombineFilters(
                TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, AzureTransactionLogStorage.ArchivalRow.MakePartitionKey(ClusterServiceId)),
                TableOperators.And,
                TableQuery.CombineFilters(TableQuery.GenerateFilterCondition("RowKey", QueryComparisons.LessThanOrEqual, AzureTransactionLogStorage.ArchivalRow.MaxRowKey),
                TableOperators.And,
                TableQuery.GenerateFilterCondition("RowKey", QueryComparisons.GreaterThanOrEqual, AzureTransactionLogStorage.ArchivalRow.MinRowKey))));
            var archivalTransactions = await logStorage.QueryArchivalRecords(archQuery);
            //query remaining commited records
            var cmQuery = new TableQuery<AzureTransactionLogStorage.ArchivalRow>().Where(TableQuery.CombineFilters(
                TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, AzureTransactionLogStorage.ArchivalRow.MakePartitionKey(ClusterServiceId)),
                TableOperators.And,
                TableQuery.CombineFilters(TableQuery.GenerateFilterCondition("RowKey", QueryComparisons.LessThanOrEqual, AzureTransactionLogStorage.CommitRow.MaxRowKey),
                    TableOperators.And,
                    TableQuery.GenerateFilterCondition("RowKey", QueryComparisons.GreaterThanOrEqual, AzureTransactionLogStorage.CommitRow.MinRowKey))));
            var commitTransactions = await logStorage.QueryArchivalRecords(cmQuery);
            //assert a subset of transaction has been archived 
            Assert.Equal(allTransactions.FindAll(r=>r.LSN <= maxSLN).Select(r => r.LSN).Distinct(), archivalTransactions.Select(tx=>tx.LSN).Distinct());
            //assert the remaining transactions still isn't archived 
            Assert.Equal(allTransactions.FindAll(r => r.LSN > maxSLN).Select(r => r.LSN).Distinct(), commitTransactions.Select(tx => tx.LSN).Distinct());
        }

        private static async Task<AzureTransactionLogStorage> StorageFactory(AzureTransactionLogOptions azureOptions, AzureTransactionArchiveLogOptions archiveOptions)
        {
            var config = new ClientConfiguration();
            var environment = SerializationTestEnvironment.InitializeWithDefaults(config);
            var azureConfig = Options.Create(azureOptions);
            AzureTransactionLogStorage storage = new AzureTransactionLogStorage(environment.SerializationManager, azureConfig, 
                Options.Create(archiveOptions), Options.Create(new ClusterOptions(){ClusterId = Guid.NewGuid().ToString(),
                    ServiceId = ClusterServiceId}));
            await storage.Initialize();
            return storage;
        }
    }
}
