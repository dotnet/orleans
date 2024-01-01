using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace DefaultCluster.Tests
{
    public class GrainFactoryTests : HostedTestClusterEnsureDefaultStarted
    {
        public GrainFactoryTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_Ambiguous()
        {
            Assert.Throws<ArgumentException>(() =>
            {
                var g = this.GrainFactory.GetGrain<IBase>(GetRandomGrainId());
            });
        }

        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public async Task GetGrain_Ambiguous_WithDefault()
        {
            var g = this.GrainFactory.GetGrain<IBase4>(GetRandomGrainId());
            Assert.False(await g.Foo());
        }

        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public async Task GetGrain_WithFullName()
        {
            var grainFullName = typeof(BaseGrain).FullName;
            var g = this.GrainFactory.GetGrain<IBase>(GetRandomGrainId(), grainFullName);
            Assert.True(await g.Foo());
        }

        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public async Task GetGrain_WithPrefix()
        {
            var g = this.GrainFactory.GetGrain<IBase>(GetRandomGrainId(), BaseGrain.GrainPrefix);
            Assert.True(await g.Foo());
        }

        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_AmbiguousPrefix()
        {
            Assert.Throws<ArgumentException>(() =>
            {
                var g = this.GrainFactory.GetGrain<IBase>(GetRandomGrainId(), "UnitTests.Grains");
            });
        }

        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_WrongPrefix()
        {
            Assert.Throws<ArgumentException>(() =>
            {
                var g = this.GrainFactory.GetGrain<IBase>(GetRandomGrainId(), "Foo");
            });
        }

        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public async Task GetGrain_Derived_NoPrefix()
        {
            var g = this.GrainFactory.GetGrain<IDerivedFromBase>(GetRandomGrainId());
            Assert.False(await g.Foo());
            Assert.True(await g.Bar());
        }

        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public async Task GetGrain_Derived_WithFullName()
        {
            var grainFullName = typeof(DerivedFromBaseGrain).FullName;
            var g = this.GrainFactory.GetGrain<IDerivedFromBase>(GetRandomGrainId(), grainFullName);
            Assert.False(await g.Foo());
            Assert.True(await g.Bar());
        }

        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public async Task GetGrain_Derived_WithFullName_FromBase()
        {
            var grainFullName = typeof(DerivedFromBaseGrain).FullName;
            var g = this.GrainFactory.GetGrain<IBase>(GetRandomGrainId(), grainFullName);
            Assert.False(await g.Foo());
        }

        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public async Task GetGrain_Derived_WithPrefix()
        {
            var g = this.GrainFactory.GetGrain<IDerivedFromBase>(GetRandomGrainId(), "UnitTests.Grains");
            Assert.False(await g.Foo());
            Assert.True(await g.Bar());
        }

        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_Derived_WithWrongPrefix()
        {
            Assert.Throws<ArgumentException>(() =>
            {
                var g = this.GrainFactory.GetGrain<IDerivedFromBase>(GetRandomGrainId(), "Foo");
            });
        }

        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public async Task GetGrain_OneImplementation_NoPrefix()
        {
            var g = this.GrainFactory.GetGrain<IBase1>(GetRandomGrainId());
            Assert.False(await g.Foo());
        }

        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public async Task GetGrain_OneImplementation_Prefix()
        {
            var grainFullName = typeof(BaseGrain1).FullName;
            var g = this.GrainFactory.GetGrain<IBase1>(GetRandomGrainId(), grainFullName);
            Assert.False(await g.Foo());
        }

        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public async Task GetGrain_MultipleUnrelatedInterfaces()
        {
            var g1 = this.GrainFactory.GetGrain<IBase3>(GetRandomGrainId());
            Assert.False(await g1.Foo());
            var g2 = this.GrainFactory.GetGrain<IBase2>(GetRandomGrainId());
            Assert.True(await g2.Bar());
        }

        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public async Task GetStringGrain()
        {
            var g = this.GrainFactory.GetGrain<IStringGrain>(Guid.NewGuid().ToString());
            Assert.True(await g.Foo());
        }

        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public async Task GetGuidGrain()
        {
            var g = this.GrainFactory.GetGrain<IGuidGrain>(Guid.NewGuid());
            Assert.True(await g.Foo());
        }
    }
}
