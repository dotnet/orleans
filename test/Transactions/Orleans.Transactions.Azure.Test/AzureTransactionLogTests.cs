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
using Orleans.Runtime.Configuration;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.AzureStorage.Storage.Development;
using TestExtensions;
using Xunit;
using Orleans.Persistence.AzureStorage;

namespace Orleans.Transactions.AzureStorage.Tests
{
    [TestCategory("Azure"), TestCategory("Transactions"), TestCategory("BVT")]
    public class AzureTransactionLogTests : IDisposable
    {
        private const string TestClusterId = "AzureTransactionLogTestCluster";
        private static string TableName = $"TransactionLog{((uint) Guid.NewGuid().GetHashCode()) % 100000}";
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
                TableName = TableName
            };
            var archiveOptions = new AzureTransactionArchiveLogOptions()
            {
                ArchiveLog = true
            };

            var logStorage = await StorageFactory(azureOptions, archiveOptions);
            var recordsNum = 2000;
            var allTransactions = new List<CommitRecord>(recordsNum);
            for (int i = 0; i< recordsNum; i++)
            {
                allTransactions.Add(new CommitRecord(){LSN=i, TransactionId = i});
            }
            await logStorage.Append(allTransactions);
            //all transactions will be archived 
            var maxSLN = recordsNum + 2;
            await logStorage.TruncateLog(maxSLN);
            //since lsn and transaction id are the same in this test, so use it as the query on row key
            var query = new TableQuery<AzureTransactionLogStorage.ArchivalRow>().Where(
                TableQuery.GenerateFilterCondition(nameof(AzureTransactionLogStorage.ArchivalRow.RowKey), QueryComparisons.LessThanOrEqual, AzureTransactionLogStorage.ArchivalRow.MakeRowKey(maxSLN)));
            var archivalTransactions = await logStorage.QueryArchivalRecords(query);
            //assert a subset of transaction has been archived and no duplicates
            Assert.Equal(allTransactions.Select(tx=>tx.LSN).Distinct(), archivalTransactions.Select(tx=>tx.LSN).Distinct());
            Assert.Equal(archivalTransactions.Select(tx => tx.LSN).Distinct().Count(), archivalTransactions.Select(tx => tx.LSN).Count());
        }

        private static async Task<AzureTransactionLogStorage> StorageFactory(AzureTransactionLogOptions azureOptions, AzureTransactionArchiveLogOptions archiveOptions)
        {
            var config = new ClientConfiguration();
            var environment = SerializationTestEnvironment.InitializeWithDefaults(config);
            var azureConfig = Options.Create(azureOptions);
            AzureTransactionLogStorage storage = new AzureTransactionLogStorage(environment.SerializationManager, azureConfig, 
                Options.Create(archiveOptions), Options.Create(new ClusterOptions(){ClusterId = TestClusterId, ServiceId = "TestServiceID"}));
            await storage.Initialize();
            return storage;
        }

        public void Dispose()
        {
            var tableManager = new AzureTableDataManager<AzureTransactionLogStorage.ArchivalRow>(TableName, TestDefaultConfiguration.DataConnectionString,
                NullLoggerFactory.Instance);
            tableManager.DeleteTableAsync().Ignore();
        }
    }
}
