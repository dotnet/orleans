using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
            : base(() => StateStorageFactory(fixture), (seed) => new TestState() { State = seed }, fixture.GrainFactory, testOutput)
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

        private static async Task<TableClient> InitTableAsync(ILogger logger)
        {
            try
            {
                var tableCreationClient = GetCloudTableCreationClient(logger);
                TableClient tableRef = tableCreationClient.GetTableClient(tableName);
                var tableItem = await tableRef.CreateIfNotExistsAsync();
                var didCreate = tableItem is not null;

                logger.LogInformation("{Verb} Azure storage table {TableName}", didCreate ? "Created" : "Attached to", tableName);
                return tableRef;
            }
            catch (Exception exc)
            {
                logger.LogError(exc, "Could not initialize connection to storage table {TableName}", tableName);
                throw;
            }
        }

        private static TableServiceClient GetCloudTableCreationClient(ILogger logger)
        {
            try
            {
                var creationClient = new TableServiceClient(TestDefaultConfiguration.DataConnectionString);
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
