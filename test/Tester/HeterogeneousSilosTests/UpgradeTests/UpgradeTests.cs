using System;
using System.Threading.Tasks;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;
using TestVersionGrainInterfaces;
using Xunit;

namespace Tester.HeterogeneousSilosTests.UpgradeTests
{
    [TestCategory("Versioning"), TestCategory("ExcludeXAML"), TestCategory("SlowBVT"), TestCategory("Functional")]
    public class MinimumVersionTests : UpgradeTestsBase
    {
        protected override VersionSelectorStrategy VersionSelectorStrategy => MinimumVersion.Singleton;
        protected override CompatibilityStrategy CompatibilityStrategy => BackwardCompatible.Singleton;
        
        [Fact]
        public Task AlwaysCreateActivationWithMinimumVersionTest()
        {
            return Step1_StartV1Silo_Step2_StartV2Silo_Step3_StopV2Silo(1);
        }
    }

    [TestCategory("Versioning"), TestCategory("ExcludeXAML"), TestCategory("SlowBVT"), TestCategory("Functional")]
    public class LatestVersionTests : UpgradeTestsBase
    {
        protected override VersionSelectorStrategy VersionSelectorStrategy => LatestVersion.Singleton;
        protected override CompatibilityStrategy CompatibilityStrategy => BackwardCompatible.Singleton;

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

    [TestCategory("Versioning"), TestCategory("ExcludeXAML"), TestCategory("SlowBVT"), TestCategory("Functional")]
    public class AllVersionsCompatibleTests : UpgradeTestsBase
    {
        protected override VersionSelectorStrategy VersionSelectorStrategy => LatestVersion.Singleton;
        protected override CompatibilityStrategy CompatibilityStrategy => AllVersionsCompatible.Singleton;

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

    [TestCategory("Versioning"), TestCategory("ExcludeXAML"), TestCategory("SlowBVT"), TestCategory("Functional")]
    public class RandomCompatibleVersionTests : UpgradeTestsBase
    {
        protected override VersionSelectorStrategy VersionSelectorStrategy => AllCompatibleVersions.Singleton;
        protected override CompatibilityStrategy CompatibilityStrategy => AllVersionsCompatible.Singleton;

        [Fact]
        public async Task CreateActivationWithBothVersionTest()
        {
            const int numberOfGrains = 100;

            await DeployCluster();
            await StartSiloV2();

            var versionCounter = new int[2];

            for (var i = 0; i < numberOfGrains; i++)
            {
                var v = await Client.GetGrain<IVersionUpgradeTestGrain>(i).GetVersion();
                versionCounter[v - 1]++;
            }

            Assert.InRange(versionCounter[0], 40, 60);
            Assert.InRange(versionCounter[1], 40, 60);
        }
    }
}
