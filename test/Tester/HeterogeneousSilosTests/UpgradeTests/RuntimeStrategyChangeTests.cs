using Orleans.Metadata;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;
using TestVersionGrainInterfaces;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Tester.HeterogeneousSilosTests.UpgradeTests
{
    [TestCategory("Versioning"), TestCategory("ExcludeXAML"), TestCategory("SlowBVT")]
    public class RuntimeStrategyChangeTests : UpgradeTestsBase
    {
        protected override Type VersionSelectorStrategy => typeof(LatestVersion);
        protected override Type CompatibilityStrategy => typeof(AllVersionsCompatible);

        [Fact]
        public async Task ChangeCompatibilityStrategy()
        {
            await StartSiloV1();
            var resolver = this.Client.ServiceProvider.GetService<GrainInterfaceTypeResolver>();
            var ifaceId = resolver.GetGrainInterfaceType(typeof(IVersionUpgradeTestGrain));

            var grainV1 = Client.GetGrain<IVersionUpgradeTestGrain>(0);
            Assert.Equal(1, await grainV1.GetVersion());

            await StartSiloV2();

            var grainV2 = Client.GetGrain<IVersionUpgradeTestGrain>(1);
            Assert.Equal(2, await grainV2.GetVersion());

            // Current policy "AllVersionsCompatible" -> no downgrade
            Assert.Equal(2, await grainV1.ProxyGetVersion(grainV2));
            Assert.Equal(2, await grainV2.GetVersion());
            Assert.Equal(1, await grainV1.GetVersion());

            await ManagementGrain.SetCompatibilityStrategy(ifaceId, StrictVersionCompatible.Singleton);

            // Current policy "StrictVersionCompatible" -> Downgrade mandatory
            Assert.Equal(1, await grainV1.ProxyGetVersion(grainV2));
            Assert.Equal(1, await grainV2.GetVersion());
            Assert.Equal(1, await grainV1.GetVersion());

            // Since this client is V1, only V1 should be activated, even with the "LatestVersion" rule
            for (var i = 2; i < 102; i++)
            {
                var grain = Client.GetGrain<IVersionUpgradeTestGrain>(i);
                Assert.Equal(1, await grain.GetVersion());
            }
            
            // Fallback to AllVersionsCompatible
            await ManagementGrain.SetCompatibilityStrategy(ifaceId, null);

            // Now we should activate only v2
            for (var i = 102; i < 202; i++)
            {
                var grain = Client.GetGrain<IVersionUpgradeTestGrain>(i);
                Assert.Equal(2, await grain.GetVersion());
            }
        }

        [Fact]
        public async Task ChangeVersionSelectorStrategy()
        {
            await StartSiloV1();
            var resolver = this.Client.ServiceProvider.GetService<GrainInterfaceTypeResolver>();
            var ifaceId = resolver.GetGrainInterfaceType(typeof(IVersionUpgradeTestGrain));

            // Only V1 exists
            var grainV1 = Client.GetGrain<IVersionUpgradeTestGrain>(0);
            Assert.Equal(1, await grainV1.GetVersion());

            await StartSiloV2();

            // Don't touch to V1
            Assert.Equal(1, await grainV1.GetVersion());
            // But only activate V2
            for (int i = 1; i < 101; i++)
            {
                var grain = Client.GetGrain<IVersionUpgradeTestGrain>(i);
                Assert.Equal(2, await grain.GetVersion());
            }

            await ManagementGrain.SetSelectorStrategy(ifaceId, MinimumVersion.Singleton);

            // Don't touch to existing activation
            Assert.Equal(1, await grainV1.GetVersion());
            for (int i = 1; i < 101; i++)
            {
                var grain = Client.GetGrain<IVersionUpgradeTestGrain>(i);
                Assert.Equal(2, await grain.GetVersion());
            }
            // But only activate V1
            for (int i = 101; i < 201; i++)
            {
                var grain = Client.GetGrain<IVersionUpgradeTestGrain>(i);
                Assert.Equal(1, await grain.GetVersion());
            }
        }

        [Fact]
        public async Task ChangeDefaultVersionCompatibilityStrategy()
        {
            Assert.Equal(typeof(AllVersionsCompatible), CompatibilityStrategy);

            await StartSiloV1();

            // Only V1 exists
            var grainV1 = new IVersionUpgradeTestGrain[2];
            grainV1[0] = Client.GetGrain<IVersionUpgradeTestGrain>(0);
            grainV1[1] = Client.GetGrain<IVersionUpgradeTestGrain>(1);
            Assert.Equal(1, await grainV1[0].GetVersion());
            Assert.Equal(1, await grainV1[1].GetVersion());

            // Change default to backward compatible
            await ManagementGrain.SetCompatibilityStrategy(BackwardCompatible.Singleton);

            await StartSiloV2();

            var grainV2 = Client.GetGrain<IVersionUpgradeTestGrain>(2);
            Assert.Equal(2, await grainV2.GetVersion());

            // Should provoke "upgrade"
            Assert.Equal(2, await grainV2.ProxyGetVersion(grainV1[0]));
            Assert.Equal(2, await grainV1[0].GetVersion());

            // Change default to backward compatible
            await ManagementGrain.SetCompatibilityStrategy(null);

            // Should not provoke upgrade
            Assert.Equal(1, await grainV2.ProxyGetVersion(grainV1[1]));
            Assert.Equal(1, await grainV1[1].GetVersion());
        }

        [Fact]
        public async Task ChangeDefaultVersionSelectorStrategy()
        {
            Assert.Equal(typeof(LatestVersion), VersionSelectorStrategy);

            await StartSiloV1();

            // Only V1 exists
            var grainV1 = Client.GetGrain<IVersionUpgradeTestGrain>(0);
            Assert.Equal(1, await grainV1.GetVersion());

            await StartSiloV2();

            // Change default to minimum version
            await ManagementGrain.SetSelectorStrategy(MinimumVersion.Singleton);

            // But only activate V1
            for (int i = 0; i < 100; i++)
            {
                var grain = Client.GetGrain<IVersionUpgradeTestGrain>(i);
                Assert.Equal(1, await grain.GetVersion());
            }

            // Change default to latest version
            await ManagementGrain.SetSelectorStrategy(null);

            // Don't touch to existing activation
            for (int i = 0; i < 100; i++)
            {
                var grain = Client.GetGrain<IVersionUpgradeTestGrain>(i);
                Assert.Equal(1, await grain.GetVersion());
            }
            // But only activate V2
            for (int i = 100; i < 200; i++)
            {
                var grain = Client.GetGrain<IVersionUpgradeTestGrain>(i);
                Assert.Equal(2, await grain.GetVersion());
            }
        }
    }
}