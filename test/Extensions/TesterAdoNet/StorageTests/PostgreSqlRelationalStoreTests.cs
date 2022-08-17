using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Tests.SqlUtils;
using UnitTests.General;
using Xunit;

namespace UnitTests.StorageTests.AdoNet
{
    [TestCategory("Persistence"), TestCategory("PostgreSql")]
    public class PostgreSqlRelationalStoreTests : RelationalStoreTestsBase, IClassFixture<PostgreSqlRelationalStoreTests.Fixture>
    {
        private const string TestDatabaseName = "OrleansStreamTest";
        private const string AdoNetInvariantName = AdoNetInvariants.InvariantNamePostgreSql;

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

        public PostgreSqlRelationalStoreTests(Fixture fixture)
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
    }
}
