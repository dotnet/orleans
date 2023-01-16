using System;
using System.Threading.Tasks;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;
using TestVersionGrainInterfaces;
using Xunit;

namespace Tester.HeterogeneousSilosTests.UpgradeTests
{
    [TestCategory("Versioning"), TestCategory("ExcludeXAML"), TestCategory("SlowBVT")]
    public class MinimumVersionTests : UpgradeTestsBase
    {
        protected override Type VersionSelectorStrategy => typeof(MinimumVersion);
        protected override Type CompatibilityStrategy => typeof(BackwardCompatible);
        
        [Fact]
        public Task AlwaysCreateActivationWithMinimumVersion()
        {
            // Even after v2 silo is deployed, we should only activate v1 grains
            return Step1_StartV1Silo_Step2_StartV2Silo_Step3_StopV2Silo(step2Version: 1);
        }
    }

    [TestCategory("Versioning"), TestCategory("ExcludeXAML"), TestCategory("SlowBVT")]
    public class LatestVersionTests : UpgradeTestsBase
    {
        protected override Type VersionSelectorStrategy => typeof(LatestVersion);
        protected override Type CompatibilityStrategy => typeof(BackwardCompatible);

        [Fact]
        public Task AlwaysCreateActivationWithLatestVersion()
        {
            // After v2 is deployed, we should always activate v2 grains
            return Step1_StartV1Silo_Step2_StartV2Silo_Step3_StopV2Silo(step2Version: 2);
        }

        [Fact]
        public Task UpgradeProxyCallNoPendingRequest()
        {
            // v2 -> v1 call should provoke grain activation upgrade.
            // The grain is inactive when receiving the message
            return ProxyCallNoPendingRequest(expectedVersion: 2);
        }

        [Fact]
        public Task UpgradeProxyCallWithPendingRequest()
        {
            // v2 -> v1 call should provoke grain activation upgrade
            // The grain is already processing a request when receiving the message
            return ProxyCallWithPendingRequest(expectedVersion: 2);
        }
    }

    [TestCategory("Versioning"), TestCategory("ExcludeXAML"), TestCategory("SlowBVT")]
    public class AllVersionsCompatibleTests : UpgradeTestsBase
    {
        protected override Type VersionSelectorStrategy => typeof(LatestVersion);
        protected override Type CompatibilityStrategy => typeof(AllVersionsCompatible);

        [Fact]
        public Task DoNotUpgradeProxyCallNoPendingRequest()
        {
            // v2 -> v1 call should provoke grain activation upgrade because they are compatible
            // The grain is inactive when receiving the message
            return ProxyCallNoPendingRequest(expectedVersion: 1);
        }

        [Fact]
        public Task DoNotUpgradeProxyCallWithPendingRequest()
        {
            // v2 -> v1 call should provoke grain activation upgrade because they are compatible
            // The grain is already processing a request when receiving the message
            return ProxyCallWithPendingRequest(expectedVersion: 1);
        }
    }

    [TestCategory("Versioning"), TestCategory("ExcludeXAML"), TestCategory("SlowBVT")]
    public class RandomCompatibleVersionTests : UpgradeTestsBase
    {
        protected override Type VersionSelectorStrategy => typeof(AllCompatibleVersions);
        protected override Type CompatibilityStrategy => typeof(AllVersionsCompatible);

        [Fact]
        public async Task CreateActivationWithBothVersion()
        {
            const float numberOfGrains = 300;

            await StartSiloV1();
            await StartSiloV2();

            var versionCounter = new int[2];

            // We should create v1 and v2 activations

            for (var i = 0; i < numberOfGrains; i++)
            {
                var v = await Client.GetGrain<IVersionUpgradeTestGrain>(i).GetVersion();
                versionCounter[v - 1]++;
            }

            // 99.95% chance of success
            Assert.InRange(versionCounter[0]/numberOfGrains, 0.35, 0.65);
            Assert.InRange(versionCounter[1]/numberOfGrains, 0.35, 0.65);
        }
    }
}