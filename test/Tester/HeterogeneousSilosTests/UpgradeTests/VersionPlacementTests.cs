using System;
using System.Threading.Tasks;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;
using TestVersionGrainInterfaces;
using Xunit;

namespace Tester.HeterogeneousSilosTests.UpgradeTests
{
    [TestCategory("Versioning"), TestCategory("ExcludeXAML"), TestCategory("SlowBVT"), TestCategory("Functional")]
    public class VersionPlacementTests : UpgradeTestsBase
    {
        protected override short SiloCount => 3;

        protected override Type VersionSelectorStrategy => typeof(AllCompatibleVersions);
        protected override Type CompatibilityStrategy => typeof(AllVersionsCompatible);

        [Fact]
        public async Task ActivateDominantVersion()
        {
            await StartSiloV1();

            var grain0 = Client.GetGrain<IVersionPlacementTestGrain>(0);
            Assert.Equal(1, await grain0.GetVersion());

            await StartSiloV2();

            for (var i = 1; i < 101; i++)
            {
                var grain1 = Client.GetGrain<IVersionPlacementTestGrain>(i);
                Assert.Equal(1, await grain1.GetVersion());
            }

            await StartSiloV2();

            for (var i = 101; i < 201; i++)
            {
                var grain2 = Client.GetGrain<IVersionPlacementTestGrain>(i);
                Assert.Equal(2, await grain2.GetVersion());
            }

        }
    }
}