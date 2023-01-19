using TestExtensions;
using UnitTests.StorageTests.Relational;
using UnitTests.StorageTests.Relational.TestDataSets;
using Xunit;
using Xunit.Abstractions;

namespace Tester.Redis.Persistence
{
    [TestCategory("Redis"), TestCategory("Persistence"), TestCategory("Functional")]
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class RedisStorageTestsOrleansSerializer
    {
        private readonly CommonFixture fixture;

        public RedisStorageTestsOrleansSerializer(ITestOutputHelper output, CommonFixture commonFixture)
        {
            TestUtils.CheckForRedis();
            this.fixture = commonFixture;
        }

        [SkippableTheory, ClassData(typeof(StorageDataSetGeneric<string, string>))]
        [TestCategory("Functional")]
        internal async Task StorageDataSetGeneric_WriteRead(int testNum)
        {
            var storageProvider = await fixture.GetStorageProvider(true);
            var commonStorageTests = new CommonStorageTests(storageProvider);
            var (grainType, getGrain, grainState) = StorageDataSetGeneric<string, string>.GetTestData(testNum);
            await commonStorageTests.Store_WriteRead(grainType, getGrain, grainState);
        }
    }
}