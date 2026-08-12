using Microsoft.Data.SqlClient;
using Orleans.Tests.SqlUtils;
using UnitTests.StorageTests.Relational;
using UnitTests.General;
using Xunit;

namespace UnitTests.StorageTests.AdoNet
{
    /// <summary>
    /// Tests for SQL Server relational storage functionality.
    /// </summary>
    [TestCategory("Persistence"), TestCategory("SqlServer")]
    [TestSuite("Functional")]
    [TestProvider("SqlServer")]
    [TestArea("Persistence")]
    public class SqlServerRelationalStoreTests : RelationalStoreTestsBase, IClassFixture<SqlServerRelationalStoreTests.Fixture>
    {
        private const string TestDatabaseName = "OrleansStreamTest";
        private const string AdoNetInvariantName = AdoNetInvariants.InvariantNameSqlServer;
        private readonly RelationalStorageForTesting _storage;

        public class Fixture
        {
            public Fixture() : this(
                () => RelationalStorageForTesting.SetupInstance(AdoNetInvariantName, TestDatabaseName).GetAwaiter().GetResult())
            {
            }

            internal Fixture(Func<RelationalStorageForTesting> storageFactory)
            {
                ArgumentNullException.ThrowIfNull(storageFactory);
                Storage = storageFactory();
            }

            public RelationalStorageForTesting Storage { get; }
        }

        public SqlServerRelationalStoreTests(Fixture fixture) : base(AdoNetInvariantName)
        {
            _storage = fixture.Storage;
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Streaming_SqlServer_Test()
        {
            using(var tokenSource = new CancellationTokenSource(StreamCancellationTimeoutLimit))
            {                
                var isMatch = await Task.WhenAll(InsertAndReadStreamsAndCheckMatch(_storage, StreamSizeToBeInsertedInBytes, NumberOfParallelStreams, tokenSource.Token));
                Assert.True(isMatch.All(i => i), "All inserted streams should be equal to read streams.");
            }
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task CancellationToken_SqlServer_Test()
        {
            await CancellationTokenTest(_storage, CancellationTestTimeoutLimit);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task DataSource_SqlServer_Test()
        {
            Skip.If(string.IsNullOrWhiteSpace(_storage.CurrentConnectionString), "Connection string not provided.");
            using var dataSource = new ProviderDbDataSource(
                _storage.CurrentConnectionString,
                () => new SqlConnection(_storage.CurrentConnectionString));
            var storage = RelationalStorage.CreateInstance(AdoNetInvariantName, dataSource);

            var values = await storage.ReadAsync(
                "SELECT 47;",
                parameterProvider: null,
                (record, _, _) => Task.FromResult(record.GetInt32(0)));

            Assert.Equal([47], values);
        }
    }

    public class SqlServerRelationalStoreFixtureTests
    {
        [Fact]
        public void InitializationFailureIsPropagated()
        {
            var expectedException = new InvalidOperationException("Simulated SQL Server database initialization failure.");

            var exception = Assert.Throws<InvalidOperationException>(
                () => new SqlServerRelationalStoreTests.Fixture(() => throw expectedException));

            Assert.Same(expectedException, exception);
        }

        [Fact]
        public void NullStorageFactoryIsRejected()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => new SqlServerRelationalStoreTests.Fixture(null!));

            Assert.Equal("storageFactory", exception.ParamName);
        }
    }
}
