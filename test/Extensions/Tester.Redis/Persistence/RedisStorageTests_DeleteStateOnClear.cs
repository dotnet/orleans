using TestExtensions;
using UnitTests.StorageTests.Relational;
using UnitTests.StorageTests.Relational.TestDataSets;
using Xunit;
using Xunit.Abstractions;

namespace Tester.Redis.Persistence
{
    [TestCategory("Redis"), TestCategory("Persistence"), TestCategory("Functional")]
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class RedisStorageTests_DeleteStateOnClear
    {
        private readonly CommonFixture fixture;
        private readonly CommonStorageTests commonStorageTests;
    
        public RedisStorageTests_DeleteStateOnClear(ITestOutputHelper output, CommonFixture commonFixture) 
        {
            TestUtils.CheckForRedis();
            this.fixture = commonFixture;
            this.commonStorageTests = new CommonStorageTests(commonFixture.CreateRedisGrainStorage(useOrleansSerializer: false, deleteStateOnClear: true).GetAwaiter().GetResult());      
        }

        [SkippableTheory, ClassData(typeof(StorageDataSet2CyrillicIdsAndGrainNames<string>))]
        [TestCategory("Functional")]
        internal async Task DataSet2_Cyrillic_WriteClearRead(int testNum)
        {
            var (grainType, getGrain, grainState) = StorageDataSet2CyrillicIdsAndGrainNames<string>.GetTestData(testNum);
            await this.commonStorageTests.Store_WriteClearRead(grainType, getGrain, grainState);
        }

        [SkippableTheory, ClassData(typeof(StorageDataSetPlain<long>))]
        [TestCategory("Functional")]
        internal async Task PersistenceStorage_StorageDataSetPlain_IntegerKey_WriteClearRead(int testNum)
        {
            var (grainType, getGrain, grainState) = StorageDataSetPlain<long>.GetTestData(testNum);
            await this.commonStorageTests.Store_WriteClearRead(grainType, getGrain, grainState);
        }

        [SkippableTheory, ClassData(typeof(StorageDataSetGeneric<Guid, string>))]
        [TestCategory("Functional")]
        internal async Task StorageDataSetGeneric_GuidKey_Generic_WriteClearRead(int testNum)
        {
            var (grainType, getGrain, grainState) = StorageDataSetGeneric<Guid, string>.GetTestData(testNum);
            await this.commonStorageTests.Store_WriteClearRead(grainType, getGrain, grainState);
        }

        [SkippableTheory, ClassData(typeof(StorageDataSetGeneric<long, string>))]
        [TestCategory("Functional")]
        internal async Task StorageDataSetGeneric_IntegerKey_Generic_WriteClearRead(int testNum)
        {
            var (grainType, getGrain, grainState) = StorageDataSetGeneric<long, string>.GetTestData(testNum);
            await this.commonStorageTests.Store_WriteClearRead(grainType, getGrain, grainState);
        }

        [SkippableTheory, ClassData(typeof(StorageDataSetGeneric<string, string>))]
        [TestCategory("Functional")]
        internal async Task StorageDataSetGeneric_StringKey_Generic_WriteClearRead(int testNum)
        {
            var (grainType, getGrain, grainState) = StorageDataSetGeneric<string, string>.GetTestData(testNum);
            await this.commonStorageTests.Store_WriteClearRead(grainType, getGrain, grainState);
        }

        [SkippableTheory, ClassData(typeof(StorageDataSetPlain<Guid>))]
        [TestCategory("Functional")]
        internal async Task StorageDataSetPlain_GuidKey_WriteClearRead(int testNum)
        {
            var (grainType, getGrain, grainState) = StorageDataSetPlain<Guid>.GetTestData(testNum);
            await this.commonStorageTests.Store_WriteClearRead(grainType, getGrain, grainState);
        }

        [SkippableTheory, ClassData(typeof(StorageDataSetPlain<string>))]
        [TestCategory("Functional")]
        internal async Task StorageDataSetPlain_StringKey_WriteClearRead(int testNum)
        {
            var (grainType, getGrain, grainState) = StorageDataSetPlain<string>.GetTestData(testNum);
            await this.commonStorageTests.Store_WriteClearRead(grainType, getGrain, grainState);
        }
    }
}