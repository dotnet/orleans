using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using Orleans.Runtime;
using Tests.GeoClusterTests;
using Xunit;
using Xunit.Abstractions;
using Orleans.Runtime.Configuration;
using Tester;

namespace Tests.GeoClusterTests
{
    public class LogConsistencyTestsFourClusters :
       IClassFixture<LogConsistencyTestsFourClusters.Fixture>
    {

        public LogConsistencyTestsFourClusters(ITestOutputHelper output, Fixture fixture)
        {
            this.fixture = fixture;
            fixture.StartClustersIfNeeded(4, output);
        }
        Fixture fixture;

        public class Fixture : LogConsistencyTestFixture
        {
        }

        const int phases = 100;

        [Fact, TestCategory("GeoCluster")]
        public async Task TestBattery_SharedStateStorageProvider()
        {
            await fixture.RunChecksOnGrainClass("TestGrains.LogTestGrainSharedStateStorage", true, phases);
        }

        [Fact, TestCategory("GeoCluster")]
        public async Task TestBattery_SharedLogStorageProvider()
        {
            await fixture.RunChecksOnGrainClass("TestGrains.LogTestGrainSharedLogStorage", true, phases);
        }

        [Fact, TestCategory("GeoCluster")]
        public async Task TestBattery_GsiDefaultStorageProvider()
        {
            await fixture.RunChecksOnGrainClass("TestGrains.GsiLogTestGrain", true, phases);
        }

        [Fact, TestCategory("GeoCluster")]
        public async Task TestBattery_MemoryStorageProvider()
        {
            await fixture.RunChecksOnGrainClass("TestGrains.LogTestGrainMemoryStorage", true, phases);
        }

        [Fact, TestCategory("GeoCluster")]
        public async Task TestBattery_CustomStorageProvider()
        {
            await fixture.RunChecksOnGrainClass("TestGrains.LogTestGrainCustomStorage", true, phases);
        }

        [Fact, TestCategory("GeoCluster")]
        public async Task TestBattery_CustomStorageProvider_PrimaryCluster()
        {
            await fixture.RunChecksOnGrainClass("TestGrains.LogTestGrainCustomStoragePrimaryCluster", false, phases);
        }

    }
}
