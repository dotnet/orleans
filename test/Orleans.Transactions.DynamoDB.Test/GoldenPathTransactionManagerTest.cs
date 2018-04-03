using System;
using System.Threading.Tasks;
using Xunit.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Configuration;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.Tests;
using TestExtensions;
using Orleans.TestingHost.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using AWSUtils.Tests.StorageTests;

namespace Orleans.Transactions.DynamoDB.Tests
{
    [TestCategory("DynamoDb"), TestCategory("Transactions"), TestCategory("Functional")]
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
            TestFixture.CheckForDynamoDBStorage();
            ITransactionManager tm = new TransactionManager(new TransactionLog(StorageFactory), Options.Create<TransactionsOptions>(new TransactionsOptions()), NullLoggerFactory.Instance, NullTelemetryProducer.Instance, Options.Create<SiloStatisticsOptions>(new SiloStatisticsOptions()), LogMaintenanceInterval);
            tm.StartAsync().GetAwaiter().GetResult();
            return tm;
        }

        private static async Task<ITransactionLogStorage> StorageFactory()
        {
            TestFixture.CheckForDynamoDBStorage();
            var config = new ClientConfiguration();
            var environment = SerializationTestEnvironment.InitializeWithDefaults(config);
            var dynamoConfig = Options.Create(new DynamoDBTransactionLogOptions()
            {
                // TODO: Find better way for test isolation.
                TableName = $"TransactionLog{((uint)Guid.NewGuid().GetHashCode()) % 100000}",
                Service = AWSTestConstants.Service
            });
            DynamoDBTransactionLogStorage storage = new DynamoDBTransactionLogStorage(environment.SerializationManager, dynamoConfig, environment.Client.ServiceProvider.GetRequiredService<ILoggerFactory>());
            await storage.Initialize();
            return storage;
        }
    }
}
