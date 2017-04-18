using System;
using System.Threading.Tasks;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Placement;
using Xunit;

namespace Tester.HeterogeneousSilosTests.UpgradeTests
{
    [TestCategory("ExcludeXAML"), TestCategory("SlowBVT"), TestCategory("Functional")]
    public class MinimumVersionTests : UpgradeTestsBase
    {
        protected override VersionPlacementStrategy VersionPlacementStrategy => MinimumVersionPlacement.Singleton;
        protected override VersionCompatibilityStrategy VersionCompatibilityStrategy => BackwardCompatible.Singleton;
        
        [Fact]
        public Task AlwaysCreateActivationWithMinimumVersionTest()
        {
            return Step1_StartV1Silo_Step2_StartV2Silo_Step3_StopV2Silo(1);
        }
    }

    [TestCategory("ExcludeXAML"), TestCategory("SlowBVT"), TestCategory("Functional")]
    public class LatestVersionTests : UpgradeTestsBase
    {
        protected override VersionPlacementStrategy VersionPlacementStrategy => LatestVersionPlacement.Singleton;
        protected override VersionCompatibilityStrategy VersionCompatibilityStrategy => BackwardCompatible.Singleton;

        [Fact]
        public Task AlwaysCreateActivationWithLatestVersionTest()
        {
            return Step1_StartV1Silo_Step2_StartV2Silo_Step3_StopV2Silo(2);
        }

        [Fact]
        public Task UpgradeProxyCallNoPendingRequestTest()
        {
            return ProxyCallNoPendingRequest(2);
        }

        [Fact]
        public Task UpgradeProxyCallWithPendingRequestTest()
        {
            return ProxyCallNoPendingRequest(2);
        }
    }

    [TestCategory("ExcludeXAML"), TestCategory("SlowBVT"), TestCategory("Functional")]
    public class AllVersionsCompatibleTests : UpgradeTestsBase
    {
        protected override VersionPlacementStrategy VersionPlacementStrategy => LatestVersionPlacement.Singleton;
        protected override VersionCompatibilityStrategy VersionCompatibilityStrategy => AllVersionsCompatible.Singleton;

        [Fact]
        public Task DoNotUpgradeProxyCallNoPendingRequestTest()
        {
            return ProxyCallNoPendingRequest(1);
        }
        [Fact]
        public Task DoNotUpgradeProxyCallWithPendingRequestTest()
        {
            return ProxyCallNoPendingRequest(1);
        }
    }
}
