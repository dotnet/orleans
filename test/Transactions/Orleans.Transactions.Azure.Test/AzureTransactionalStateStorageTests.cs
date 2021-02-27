using System;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos.Table;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.AzureStorage;
using Orleans.Transactions.AzureStorage.Tests;
using Orleans.Transactions.TestKit.xUnit;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.Azure.Tests
{
    public class TestState : IEquatable<TestState>
    {
        public int State { get; set; }

        public bool Equals(TestState other)
        {
            return other == null?false:this.State.Equals(other.State);
        }
    }

    public class AzureTransactionalStateStorageTests : TransactionalStateStorageTestRunnerxUnit<TestState>, IClassFixture<TestFixture>
    {
        private const string tableName = "StateStorageTests";
        private const string partition = "testpartition";
        public AzureTransactionalStateStorageTests(TestFixture fixture, ITestOutputHelper testOutput)
            :base(()=>StateStorageFactory(fixture), (seed)=>new TestState(){State = seed}, fixture.GrainFactory, testOutput)
        {
        }

        private static async Task<ITransactionalStateStorage<TestState>> StateStorageFactory(TestFixture fixture)
        {
            var table = await InitTableAsync(NullLogger.Instance);
            var jsonSettings = TransactionalStateFactory.GetJsonSerializerSettings(fixture.HostedCluster.ServiceProvider);
            var stateStorage = new AzureTableTransactionalStateStorage<TestState>(table, $"{partition}{DateTime.UtcNow.Ticks}", jsonSettings,
                NullLoggerFactory.Instance.CreateLogger<AzureTableTransactionalStateStorage<TestState>>());
            return stateStorage;
        }

        private static async Task<CloudTable> InitTableAsync(ILogger logger)
        {
            try
            {
                CloudTableClient tableCreationClient = GetCloudTableCreationClient(logger);
                CloudTable tableRef = tableCreationClient.GetTableReference(tableName);
                bool didCreate = await tableRef.CreateIfNotExistsAsync();


                logger.Info($"{(didCreate ? "Created" : "Attached to")} Azure storage table {tableName}", (didCreate ? "Created" : "Attached to"));
                return tableRef;
            }
            catch (Exception exc)
            {
                logger.LogError($"Could not initialize connection to storage table {tableName}", exc);
                throw;
            }
        }

        private static CloudTableClient GetCloudTableCreationClient(ILogger logger)
        {
            try
            {
                CloudStorageAccount storageAccount = CloudStorageAccount.Parse(TestDefaultConfiguration.DataConnectionString);
                CloudTableClient creationClient = storageAccount.CreateCloudTableClient();
                // Values supported can be AtomPub, Json, JsonFullMetadata or JsonNoMetadata with Json being the default value
                creationClient.DefaultRequestOptions.PayloadFormat = TablePayloadFormat.JsonNoMetadata;
                return creationClient;
            }
            catch (Exception exc)
            {
                logger.LogError("Error creating CloudTableCreationClient.", exc);
                throw;
            }
        }
    }
}
