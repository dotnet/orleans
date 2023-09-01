using Orleans.Tests.SqlUtils;
using UnitTests.General;
using Xunit;

namespace UnitTests.StorageTests.AdoNet
{
    [TestCategory("Persistence"), TestCategory("SqlServer")]
    public class SqlServerRelationalStoreTests : RelationalStoreTestsBase, IClassFixture<SqlServerRelationalStoreTests.Fixture>
    {
        private const string TestDatabaseName = "OrleansStreamTest";
        private const string AdoNetInvariantName = AdoNetInvariants.InvariantNameSqlServer;
        private readonly RelationalStorageForTesting _storage;

        public class Fixture
        {
            public Fixture()
            {
                try
                {
                    Storage = RelationalStorageForTesting.SetupInstance(AdoNetInvariantName, TestDatabaseName).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to initialize {AdoNetInvariantName} for testing: {ex}");
                }
            }

            public RelationalStorageForTesting Storage { get; private set; }
        }

        public SqlServerRelationalStoreTests(Fixture fixture)
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
    }
}
