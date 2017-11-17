﻿using System;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Runtime.Configuration;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.Tests;
using TestExtensions;
using Orleans.TestingHost.Utils;

namespace Orleans.Transactions.Azure.Tests
{
    [TestCategory("Azure"), TestCategory("Transactions"), TestCategory("Functional")]
    public class GoldenPathTransactionManagerTest : GoldenPathTransactionManagerTestRunner
    {
        private static readonly TimeSpan LogMaintenanceInterval = TimeSpan.FromMilliseconds(10);
        private static readonly TimeSpan StorageDelay = TimeSpan.FromSeconds(30);

        public GoldenPathTransactionManagerTest(ITestOutputHelper output)
            : base(MakeTransactionManager(), LogMaintenanceInterval, StorageDelay, output)
        {
        }

        private static ITransactionManager MakeTransactionManager()
        {
            TestFixture.CheckForAzureStorage(TestDefaultConfiguration.DataConnectionString);
            ITransactionManager tm = new TransactionManager(new TransactionLog(StorageFactory), Options.Create<TransactionsConfiguration>(new TransactionsConfiguration()), NullLoggerFactory.Instance, NullTelemetryProducer.Instance, () => new NodeConfiguration(), LogMaintenanceInterval);
            tm.StartAsync().GetAwaiter().GetResult();
            return tm;
        }

        private static async Task<ITransactionLogStorage> StorageFactory()
        {
            TestFixture.CheckForAzureStorage(TestDefaultConfiguration.DataConnectionString);
            var config = new ClientConfiguration();
            var environment = SerializationTestEnvironment.InitializeWithDefaults(config);
            var azureConfig = Options.Create(new AzureTransactionLogConfiguration()
            {
                // TODO: Find better way for test isolation.
                TableName = $"TransactionLog{((uint)Guid.NewGuid().GetHashCode()) % 100000}",
                ConnectionString = TestDefaultConfiguration.DataConnectionString
            });
            AzureTransactionLogStorage storage = new AzureTransactionLogStorage(environment.SerializationManager, azureConfig);
            await storage.Initialize();
            return storage;
        }
    }
}
