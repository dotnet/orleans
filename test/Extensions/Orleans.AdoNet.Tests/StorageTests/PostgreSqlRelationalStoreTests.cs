using Orleans.Tests.SqlUtils;
using UnitTests.General;
using Xunit;

namespace UnitTests.StorageTests.AdoNet
{
    /// <summary>
    /// Tests for PostgreSQL relational storage functionality.
    /// </summary>
    [TestCategory("Persistence"), TestCategory("PostgreSql")]
    [TestSuite("Functional")]
    [TestProvider("PostgreSql")]
    [TestArea("Persistence")]
    public class PostgreSqlRelationalStoreTests : RelationalStoreTestsBase, IClassFixture<PostgreSqlRelationalStoreTests.Fixture>
    {
        private const string TestDatabaseName = "OrleansStreamTest";
        private const string AdoNetInvariantName = AdoNetInvariants.InvariantNamePostgreSql;

        private readonly RelationalStorageForTesting _storage;

        public class Fixture : IAsyncLifetime
        {
            public RelationalStorageForTesting Storage { get; private set; } = null!;

            public async Task InitializeAsync()
            {
                Storage = await RelationalStorageForTesting.SetupInstance(AdoNetInvariantName, TestDatabaseName);
            }

            public Task DisposeAsync() => Task.CompletedTask;
        }

        public PostgreSqlRelationalStoreTests(Fixture fixture) : base(AdoNetInvariantName)
        {
            _storage = fixture.Storage;
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Streaming_PostgreSql_Test()
        {
            using(var tokenSource = new CancellationTokenSource(StreamCancellationTimeoutLimit))
            {             
                var isMatch = await Task.WhenAll(InsertAndReadStreamsAndCheckMatch(_storage, StreamSizeToBeInsertedInBytes, NumberOfParallelStreams, tokenSource.Token));
                Assert.True(isMatch.All(i => i), "All inserted streams should be equal to read streams.");
            }
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task CancellationToken_PostgreSql_Test()
        {
            await CancellationTokenTest(_storage, CancellationTestTimeoutLimit);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task NativeDataSource_PostgreSql_Test()
        {
            Skip.If(string.IsNullOrWhiteSpace(_storage.CurrentConnectionString), "Connection string not provided.");
            await using var dataSource = Npgsql.NpgsqlDataSource.Create(_storage.CurrentConnectionString);
            var storage = RelationalStorage.CreateInstance(AdoNetInvariantName, dataSource);

            var values = await storage.ReadAsync(
                "SELECT 47;",
                parameterProvider: null,
                (record, _, _) => Task.FromResult(record.GetInt32(0)));

            Assert.Equal([47], values);
        }
    }
}
