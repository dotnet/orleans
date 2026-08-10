using System.Runtime.ExceptionServices;
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
    public class MySqlRelationalStoreTests : RelationalStoreTestsBase, IClassFixture<MySqlRelationalStoreTests.Fixture>
    {
        private const string TestDatabaseName = "OrleansStreamTest";
        private const string AdoNetInvariantName = AdoNetInvariants.InvariantNameMySql;

        private readonly RelationalStorageForTesting _storage;

        public class Fixture
        {
            private readonly ExceptionDispatchInfo? preconditionsException;

            public void EnsurePreconditionsMet()
            {
                this.preconditionsException?.Throw();
            }

            public Fixture()
            {
                try
                {
                    Storage = RelationalStorageForTesting.SetupInstance(AdoNetInvariantName, TestDatabaseName).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    this.preconditionsException = ExceptionDispatchInfo.Capture(ex);
                    return;
                }
            }

            public RelationalStorageForTesting? Storage { get; private set; }
        }

        public MySqlRelationalStoreTests(Fixture fixture) : base(AdoNetInvariantName)
        {
            fixture.EnsurePreconditionsMet();
            _storage = fixture.Storage!;
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Streaming_MySql_Test()
        {
            using(var tokenSource = new CancellationTokenSource(StreamCancellationTimeoutLimit))
            {             
                var isMatch = await Task.WhenAll(InsertAndReadStreamsAndCheckMatch(_storage, StreamSizeToBeInsertedInBytes, NumberOfParallelStreams, tokenSource.Token));
                Assert.True(isMatch.All(i => i), "All inserted streams should be equal to read streams.");
            }
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task CancellationToken_MySql_Test()
        {
            await CancellationTokenTest(_storage, CancellationTestTimeoutLimit);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task DataSource_MySql_Test()
        {
            using var dataSource = new ProviderDbDataSource(
                _storage.CurrentConnectionString,
                () => new MySqlConnection(_storage.CurrentConnectionString));
            var storage = RelationalStorage.CreateInstance(AdoNetInvariantName, dataSource);

            var values = await storage.ReadAsync(
                "SELECT 47;",
                parameterProvider: null,
                (record, _, _) => Task.FromResult(record.GetInt32(0)));

            Assert.Equal([47], values);
        }
    }
}
