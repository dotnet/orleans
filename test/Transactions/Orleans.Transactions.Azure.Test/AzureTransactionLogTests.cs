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
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.AzureStorage.Tests
{
    [TestCategory("Azure"), TestCategory("Transactions"), TestCategory("BVT")]
    public class AzureTransactionLogTests 
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
                ArchiveLog = true,
                ConnectionString = TestDefaultConfiguration.DataConnectionString,
                TableName = TableName
            };

            var logStorage = await StorageFactory(azureOptions);
            var recordsNum = 2000;
            var allTransactions = new List<CommitRecord>(recordsNum);
            for (int i = 0; i< recordsNum; i++)
            {
                allTransactions.Add(new CommitRecord(){LSN=i, TransactionId = i});
            }
            await logStorage.Append(allTransactions);
            //a subset of all transactions will be archived 
            await logStorage.TruncateLog(recordsNum + 2);
            var query = new TableQuery<AzureTransactionLogStorage.ArchivalRow>().Where(
                TableQuery.GenerateFilterCondition(nameof(AzureTransactionLogStorage.ArchivalRow.ClusterId), QueryComparisons.Equal, TestClusterId));
            var archivalTransactions = await logStorage.QueryArchivalRecords(query);
            //assert a subset of transaction has been archived and no duplicates
            Assert.True(allTransactions.Select(tx=>tx.LSN).ToImmutableHashSet().IsSubsetOf(archivalTransactions.Select(tx=>tx.LSN)));
            Assert.Equal(archivalTransactions.Select(tx => tx.LSN).Count(), archivalTransactions.Select(tx => tx.LSN).ToImmutableHashSet().Count);
        }

        private static async Task<AzureTransactionLogStorage> StorageFactory(AzureTransactionLogOptions azureOptions)
        {
            var config = new ClientConfiguration();
            var environment = SerializationTestEnvironment.InitializeWithDefaults(config);
            var azureConfig = Options.Create(azureOptions);
            AzureTransactionLogStorage storage = new AzureTransactionLogStorage(environment.SerializationManager, azureConfig, Options.Create(new ClusterOptions(){ClusterId = TestClusterId}));
            await storage.Initialize();
            return storage;
        }
    }
}
