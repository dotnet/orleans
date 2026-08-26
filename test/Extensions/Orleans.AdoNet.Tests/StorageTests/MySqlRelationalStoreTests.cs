using MySql.Data.MySqlClient;
using Orleans.Tests.SqlUtils;
using TestExtensions;
using UnitTests.General;
using UnitTests.StorageTests.Relational;
using Xunit;

namespace UnitTests.StorageTests.AdoNet
{
    /// <summary>
    /// Tests for MySQL relational storage functionality.
    /// </summary>
    [TestCategory("Persistence"), TestCategory("MySql")]
    [TestSuite("Functional")]
    [TestProvider("MySql")]
    [TestArea("Persistence")]
    public class MySqlRelationalStoreTests : RelationalStoreTestsBase, IClassFixture<MySqlRelationalStoreTests.Fixture>
    {
        private const string TestDatabaseName = "OrleansStreamTest";
        private const string AdoNetInvariantName = AdoNetInvariants.InvariantNameMySql;

        private readonly RelationalStorageForTesting _storage;

        public class Fixture : IAsyncLifetime
        {
            public RelationalStorageForTesting Storage { get; private set; } = null!;

            public async ValueTask InitializeAsync()
            {
                Storage = await RelationalStorageForTesting.SetupInstance(
                    AdoNetInvariantName,
                    TestDatabaseName,
                    cancellationToken: TestContext.Current.CancellationToken);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        public MySqlRelationalStoreTests(Fixture fixture) : base(AdoNetInvariantName)
        {
            _storage = fixture.Storage;
        }

        [Fact, TestCategory("Functional")]
        public async Task Streaming_MySql_Test()
        {
            using(var tokenSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
            {             
                tokenSource.CancelAfter(StreamCancellationTimeoutLimit);
                var isMatch = await Task.WhenAll(InsertAndReadStreamsAndCheckMatch(_storage, StreamSizeToBeInsertedInBytes, NumberOfParallelStreams, tokenSource.Token));
                Assert.True(isMatch.All(i => i), "All inserted streams should be equal to read streams.");
            }
        }

        [Fact, TestCategory("Functional")]
        public async Task CancellationToken_MySql_Test()
        {
            await CancellationTokenTest(_storage, CancellationTestTimeoutLimit, TestContext.Current.CancellationToken);
        }

        [Fact, TestCategory("Functional")]
        public async Task DataSource_MySql_Test()
        {
            using var dataSource = new ProviderDbDataSource(
                _storage.CurrentConnectionString,
                () => new MySqlConnection(_storage.CurrentConnectionString));
            var storage = RelationalStorage.CreateInstance(AdoNetInvariantName, dataSource);

            var values = await storage.ReadAsync(
                "SELECT 47;",
                parameterProvider: null,
                (record, _, _) => Task.FromResult(record.GetInt32(0)),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal([47], values);
        }
    }
}
