using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Tests.GeoClusterTests
{
    [TestCategory("GeoCluster")]
    public class LogConsistencyTestsTwoClusters: IClassFixture<LogConsistencyTestsTwoClusters.Fixture>
    {
        const int phases = 100;
        private ITestOutputHelper output;
        private Fixture fixture;

        public LogConsistencyTestsTwoClusters(ITestOutputHelper output, Fixture fixture) 
        {
            fixture.EnsurePreconditionsMet();
            this.fixture = fixture;
            this.output = output;
            fixture.StartClustersIfNeeded(2, output);
        }

        public class Fixture : LogConsistencyTestFixture
        {
        }

        [SkippableFact]
        public async Task TestBattery_SharedStateStorageProvider()
        {
            await fixture.RunChecksOnGrainClass("TestGrains.LogTestGrainSharedStateStorage", true, phases, output);
        }

        [SkippableFact]
        public async Task TestBattery_SharedLogStorageProvider()
        {
            await fixture.RunChecksOnGrainClass("TestGrains.LogTestGrainSharedLogStorage", true, phases, output);
        }

        [SkippableFact]
        public async Task TestBattery_GsiDefaultStorageProvider()
        {
            await fixture.RunChecksOnGrainClass("TestGrains.GsiLogTestGrain", true, phases, output);
        }

        [SkippableFact(Skip = "https://github.com/dotnet/orleans/issues/4293"), TestCategory("Functional")]
        public async Task TestBattery_MemoryStorageProvider()
        {
            await fixture.RunChecksOnGrainClass("TestGrains.LogTestGrainMemoryStorage", true, phases, output);
        }

        [SkippableFact]
        public async Task TestBattery_CustomStorageProvider()
        {
            await fixture.RunChecksOnGrainClass("TestGrains.LogTestGrainCustomStorage", true, phases, output);
        }

        [SkippableFact]
        public async Task TestBattery_CustomStorageProvider_PrimaryCluster()
        {
            await fixture.RunChecksOnGrainClass("TestGrains.LogTestGrainCustomStoragePrimaryCluster", false, phases, output);
        }
    }
}
