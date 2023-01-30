using TestExtensions;
using UnitTests.StorageTests.Relational;
using UnitTests.StorageTests.Relational.TestDataSets;
using Xunit;
using Xunit.Abstractions;

namespace Tester.Redis.Persistence
{
    [TestCategory("Redis"), TestCategory("Persistence"), TestCategory("Functional")]
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class RedisStorageTests_OrleansSerializer
    {
        private readonly CommonFixture fixture;
        private readonly CommonStorageTests commonStorageTests;

        public RedisStorageTests_OrleansSerializer(ITestOutputHelper output, CommonFixture commonFixture)
        {
            TestUtils.CheckForRedis();
            this.fixture = commonFixture;
            var storageProvider = fixture.CreateRedisGrainStorage(true).Result;
            this.commonStorageTests = new CommonStorageTests(storageProvider);
        }

        [SkippableFact]
        [TestCategory("Functional")]
        public async Task WriteInconsistentFailsWithIncosistentStateException()
        {
            await Relational_WriteInconsistentFailsWithIncosistentStateException();
        }

        [SkippableFact]
        [TestCategory("Functional")]
        public async Task WriteRead100StatesInParallel()
        {
            await Relational_WriteReadWriteRead100StatesInParallel();
        }
        internal Task Relational_WriteReadWriteRead100StatesInParallel()
        {
            return commonStorageTests.PersistenceStorage_WriteReadWriteReadStatesInParallel(nameof(Relational_WriteReadWriteRead100StatesInParallel));
        }

        [SkippableFact]
        [TestCategory("Functional")]
        public async Task WriteReadCyrillic()
        {
            await commonStorageTests.PersistenceStorage_Relational_WriteReadIdCyrillic();
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

        [SkippableTheory, ClassData(typeof(StorageDataSetGeneric<string, string>))]
        [TestCategory("Functional")]
        internal async Task StorageDataSetGeneric_WriteRead(int testNum)
        {
            var (grainType, getGrain, grainState) = StorageDataSetGeneric<string, string>.GetTestData(testNum);
            await commonStorageTests.Store_WriteRead(grainType, getGrain, grainState);
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

        [SkippableFact]
        [TestCategory("Functional")]
        public async Task PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException()
        {
            await Relational_WriteDuplicateFailsWithInconsistentStateException();
        }

        internal async Task Relational_WriteDuplicateFailsWithInconsistentStateException()
        {
            var exception = await commonStorageTests.PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException();
            CommonStorageUtilities.AssertRelationalInconsistentExceptionMessage(exception.Message);
        }

        internal async Task Relational_WriteInconsistentFailsWithIncosistentStateException()
        {
            var exception = await commonStorageTests.PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException();
            CommonStorageUtilities.AssertRelationalInconsistentExceptionMessage(exception.Message);
        }
    }
}