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
            await fixture.RunChecksOnGrainClass("UnitTests.Grains.LogConsistentGrainSharedStateStorage", true, phases);
        }

        [Fact, TestCategory("GeoCluster")]
        public async Task TestBattery_SharedLogStorageProvider()
        {
            await fixture.RunChecksOnGrainClass("UnitTests.Grains.LogConsistentGrainSharedLogStorage", true, phases);
        }

        [Fact, TestCategory("GeoCluster")]
        public async Task TestBattery_GsiDefaultStorageProvider()
        {
            await fixture.RunChecksOnGrainClass("UnitTests.Grains.GsiLogConsistentGrain", true, phases);
        }

        [Fact, TestCategory("GeoCluster")]
        public async Task TestBattery_MemoryStorageProvider()
        {
            await fixture.RunChecksOnGrainClass("UnitTests.Grains.LogConsistentGrainMemoryStorage", true, phases);
        }

        [Fact, TestCategory("GeoCluster")]
        public async Task TestBattery_CustomStorageProvider()
        {
            await fixture.RunChecksOnGrainClass("UnitTests.Grains.LogConsistentGrainCustomStorage", true, phases);
        }

        [Fact, TestCategory("GeoCluster")]
        public async Task TestBattery_CustomStorageProvider_PrimaryCluster()
        {
            await fixture.RunChecksOnGrainClass("UnitTests.Grains.LogConsistentGrainCustomStoragePrimaryCluster", false, phases);
        }

    }
}
