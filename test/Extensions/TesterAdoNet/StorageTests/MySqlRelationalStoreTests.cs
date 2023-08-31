using Orleans.Tests.SqlUtils;
using UnitTests.General;
using Xunit;

namespace UnitTests.StorageTests.AdoNet
{
    [TestCategory("Persistence"), TestCategory("MySql")]
    public class MySqlRelationalStoreTests : RelationalStoreTestsBase, IClassFixture<MySqlRelationalStoreTests.Fixture>
    {
        private const string TestDatabaseName = "OrleansStreamTest";
        private const string AdoNetInvariantName = AdoNetInvariants.InvariantNameMySql;

        private readonly RelationalStorageForTesting _storage;

        public class Fixture
        {
            public Fixture()
            {
                Storage = RelationalStorageForTesting.SetupInstance(AdoNetInvariantName, TestDatabaseName).GetAwaiter().GetResult();
            }

            public RelationalStorageForTesting Storage { get; private set; }
        }

        public MySqlRelationalStoreTests(Fixture fixture)
        {
            _storage = fixture.Storage;
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
    }
}
