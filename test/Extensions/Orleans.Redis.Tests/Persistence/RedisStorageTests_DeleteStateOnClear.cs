using Orleans.Storage;
using TestExtensions;
using Orleans.Persistence.TestKit;
using UnitTests.StorageTests.Relational;
using UnitTests.StorageTests.Relational.TestDataSets;
using Xunit;

namespace Tester.Redis.Persistence
{
    /// <summary>
    /// Tests for Redis grain storage provider with the delete-state-on-clear option enabled.
    /// </summary>
    [TestCategory("Redis"), TestCategory("Persistence"), TestCategory("Functional")]
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    [TestSuite("Functional")]
    [TestProvider("Redis")]
    [TestArea("Persistence")]
    public class RedisStorageTests_DeleteStateOnClear
    {
        private readonly CommonFixture fixture;
        private readonly CommonStorageTests commonStorageTests;
        private readonly ITestOutputHelper output;
        private readonly IGrainStorage storageProvider;

        public RedisStorageTests_DeleteStateOnClear(ITestOutputHelper output, CommonFixture commonFixture)
        {
            TestUtils.CheckForRedis();
            this.fixture = commonFixture;
            this.output = output;
            this.storageProvider = commonFixture.CreateRedisGrainStorage(
                useOrleansSerializer: false,
                deleteStateOnClear: true,
                cancellationToken: TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            this.commonStorageTests = new CommonStorageTests(storageProvider);
        }

        [Theory, ClassData(typeof(StorageDataSet2CyrillicIdsAndGrainNames<string>))]
        [TestCategory("Functional")]
        internal async Task DataSet2_Cyrillic_WriteClearRead(int testNum)
        {
            var (grainType, getGrain, grainState) = StorageDataSet2CyrillicIdsAndGrainNames<string>.GetTestData(testNum);
            await this.commonStorageTests.Store_WriteClearRead(grainType, getGrain, grainState);
        }

        [Theory, ClassData(typeof(StorageDataSetPlain<long>))]
        [TestCategory("Functional")]
        internal async Task PersistenceStorage_StorageDataSetPlain_IntegerKey_WriteClearRead(int testNum)
        {
            var (grainType, getGrain, grainState) = StorageDataSetPlain<long>.GetTestData(testNum);
            await this.commonStorageTests.Store_WriteClearRead(grainType, getGrain, grainState);
        }

        [Theory, ClassData(typeof(StorageDataSetGeneric<Guid, string>))]
        [TestCategory("Functional")]
        internal async Task StorageDataSetGeneric_GuidKey_Generic_WriteClearRead(int testNum)
        {
            var (grainType, getGrain, grainState) = StorageDataSetGeneric<Guid, string>.GetTestData(testNum);
            await this.commonStorageTests.Store_WriteClearRead(grainType, getGrain, grainState);
        }

        [Theory, ClassData(typeof(StorageDataSetGeneric<long, string>))]
        [TestCategory("Functional")]
        internal async Task StorageDataSetGeneric_IntegerKey_Generic_WriteClearRead(int testNum)
        {
            var (grainType, getGrain, grainState) = StorageDataSetGeneric<long, string>.GetTestData(testNum);
            await this.commonStorageTests.Store_WriteClearRead(grainType, getGrain, grainState);
        }

        [Theory, ClassData(typeof(StorageDataSetGeneric<string, string>))]
        [TestCategory("Functional")]
        internal async Task StorageDataSetGeneric_StringKey_Generic_WriteClearRead(int testNum)
        {
            var (grainType, getGrain, grainState) = StorageDataSetGeneric<string, string>.GetTestData(testNum);
            await this.commonStorageTests.Store_WriteClearRead(grainType, getGrain, grainState);
        }

        [Theory, ClassData(typeof(StorageDataSetPlain<Guid>))]
        [TestCategory("Functional")]
        internal async Task StorageDataSetPlain_GuidKey_WriteClearRead(int testNum)
        {
            var (grainType, getGrain, grainState) = StorageDataSetPlain<Guid>.GetTestData(testNum);
            await this.commonStorageTests.Store_WriteClearRead(grainType, getGrain, grainState);
        }

        [Theory, ClassData(typeof(StorageDataSetPlain<string>))]
        [TestCategory("Functional")]
        internal async Task StorageDataSetPlain_StringKey_WriteClearRead(int testNum)
        {
            var (grainType, getGrain, grainState) = StorageDataSetPlain<string>.GetTestData(testNum);
            await this.commonStorageTests.Store_WriteClearRead(grainType, getGrain, grainState);
        }

        [Fact, TestCategory("Functional"), TestCategory("ModelBased")]
        public async Task GrainStorage_ModelBasedGeneratedConformance()
        {
            var runner = new GrainStorageModelBasedTestRunner(storageProvider, "RedisDeleteStateOnClear", output.WriteLine);

            await runner.RunGeneratedConformanceTests(TestContext.Current.CancellationToken);
        }
    }

}