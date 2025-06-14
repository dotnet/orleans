using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace DefaultCluster.Tests
{
    /// <summary>
    /// Tests for Orleans' grain factory and grain type resolution mechanisms.
    /// Validates how the grain factory resolves grain implementations from interfaces,
    /// handles ambiguous type mappings, supports grain class prefixes and full names,
    /// and manages inheritance hierarchies in grain type resolution.
    /// </summary>
    public class GrainFactoryTests : HostedTestClusterEnsureDefaultStarted
    {
        public GrainFactoryTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        /// <summary>
        /// Tests that ambiguous grain type resolution throws an exception.
        /// When multiple grain implementations exist for an interface without explicit resolution,
        /// Orleans should throw an ArgumentException to prevent undefined behavior.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_Ambiguous()
        {
            Assert.Throws<ArgumentException>(() =>
            {
                var g = this.GrainFactory.GetGrain<IBase>(GetRandomGrainId());
            });
        }

        /// <summary>
        /// Tests grain resolution when a default implementation is specified.
        /// Validates that Orleans correctly uses the default grain implementation
        /// when multiple implementations exist but one is marked as default.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public async Task GetGrain_Ambiguous_WithDefault()
        {
            var g = this.GrainFactory.GetGrain<IBase4>(GetRandomGrainId());
            Assert.False(await g.Foo());
        }

        /// <summary>
        /// Tests grain resolution using fully qualified type names.
        /// Validates that specifying the full grain class name correctly resolves
        /// to the intended implementation when ambiguity exists.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public async Task GetGrain_WithFullName()
        {
            var grainFullName = typeof(BaseGrain).FullName;
            var g = this.GrainFactory.GetGrain<IBase>(GetRandomGrainId(), grainFullName);
            Assert.True(await g.Foo());
        }

        /// <summary>
        /// Tests grain resolution using grain class name prefixes.
        /// Validates that Orleans can resolve grains using partial type names (prefixes)
        /// as a convenience feature for grain identification.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public async Task GetGrain_WithPrefix()
        {
            var g = this.GrainFactory.GetGrain<IBase>(GetRandomGrainId(), BaseGrain.GrainPrefix);
            Assert.True(await g.Foo());
        }

        /// <summary>
        /// Tests that ambiguous grain prefixes throw an exception.
        /// When a prefix matches multiple grain implementations, Orleans should
        /// throw an ArgumentException to prevent ambiguous resolution.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_AmbiguousPrefix()
        {
            Assert.Throws<ArgumentException>(() =>
            {
                var g = this.GrainFactory.GetGrain<IBase>(GetRandomGrainId(), "UnitTests.Grains");
            });
        }

        /// <summary>
        /// Tests that non-existent grain prefixes throw an exception.
        /// Validates proper error handling when attempting to resolve grains
        /// with prefixes that don't match any registered implementations.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_WrongPrefix()
        {
            Assert.Throws<ArgumentException>(() =>
            {
                var g = this.GrainFactory.GetGrain<IBase>(GetRandomGrainId(), "Foo");
            });
        }

        /// <summary>
        /// Tests grain resolution for derived grain interfaces without prefixes.
        /// Validates that Orleans correctly resolves grain implementations that
        /// implement derived interfaces in inheritance hierarchies.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public async Task GetGrain_Derived_NoPrefix()
        {
            var g = this.GrainFactory.GetGrain<IDerivedFromBase>(GetRandomGrainId());
            Assert.False(await g.Foo());
            Assert.True(await g.Bar());
        }

        /// <summary>
        /// Tests grain resolution for derived grains using full type names.
        /// Validates explicit resolution of derived grain implementations
        /// when specified by their complete type name.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public async Task GetGrain_Derived_WithFullName()
        {
            var grainFullName = typeof(DerivedFromBaseGrain).FullName;
            var g = this.GrainFactory.GetGrain<IDerivedFromBase>(GetRandomGrainId(), grainFullName);
            Assert.False(await g.Foo());
            Assert.True(await g.Bar());
        }

        /// <summary>
        /// Tests accessing derived grain implementations through base interfaces.
        /// Validates that a derived grain can be accessed through its base interface
        /// when explicitly specified by full name, supporting polymorphic grain usage.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public async Task GetGrain_Derived_WithFullName_FromBase()
        {
            var grainFullName = typeof(DerivedFromBaseGrain).FullName;
            var g = this.GrainFactory.GetGrain<IBase>(GetRandomGrainId(), grainFullName);
            Assert.False(await g.Foo());
        }

        /// <summary>
        /// Tests grain resolution for derived grains using prefixes.
        /// Validates that prefix-based resolution works correctly with
        /// grain inheritance hierarchies.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public async Task GetGrain_Derived_WithPrefix()
        {
            var g = this.GrainFactory.GetGrain<IDerivedFromBase>(GetRandomGrainId(), "UnitTests.Grains");
            Assert.False(await g.Foo());
            Assert.True(await g.Bar());
        }

        /// <summary>
        /// Tests error handling for invalid prefixes with derived grains.
        /// Validates that incorrect prefixes throw appropriate exceptions
        /// even in inheritance scenarios.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_Derived_WithWrongPrefix()
        {
            Assert.Throws<ArgumentException>(() =>
            {
                var g = this.GrainFactory.GetGrain<IDerivedFromBase>(GetRandomGrainId(), "Foo");
            });
        }

        /// <summary>
        /// Tests automatic grain resolution when only one implementation exists.
        /// Validates that Orleans can automatically resolve the single implementation
        /// of an interface without requiring prefixes or full names.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public async Task GetGrain_OneImplementation_NoPrefix()
        {
            var g = this.GrainFactory.GetGrain<IBase1>(GetRandomGrainId());
            Assert.False(await g.Foo());
        }

        /// <summary>
        /// Tests explicit grain resolution for single implementations.
        /// Validates that full type names work correctly even when automatic
        /// resolution would suffice (single implementation scenario).
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public async Task GetGrain_OneImplementation_Prefix()
        {
            var grainFullName = typeof(BaseGrain1).FullName;
            var g = this.GrainFactory.GetGrain<IBase1>(GetRandomGrainId(), grainFullName);
            Assert.False(await g.Foo());
        }

        /// <summary>
        /// Tests grains implementing multiple unrelated interfaces.
        /// Validates that Orleans correctly handles grains that implement multiple
        /// independent interfaces, allowing access through any implemented interface.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public async Task GetGrain_MultipleUnrelatedInterfaces()
        {
            var g1 = this.GrainFactory.GetGrain<IBase3>(GetRandomGrainId());
            Assert.False(await g1.Foo());
            var g2 = this.GrainFactory.GetGrain<IBase2>(GetRandomGrainId());
            Assert.True(await g2.Bar());
        }

        /// <summary>
        /// Tests grain creation with string-based keys.
        /// Validates that the grain factory correctly handles grains identified
        /// by string keys rather than integer or GUID keys.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public async Task GetStringGrain()
        {
            var g = this.GrainFactory.GetGrain<IStringGrain>(Guid.NewGuid().ToString());
            Assert.True(await g.Foo());
        }

        /// <summary>
        /// Tests grain creation with GUID-based keys.
        /// Validates that the grain factory correctly handles grains identified
        /// by GUID keys, supporting different grain key type scenarios.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Factory"), TestCategory("GetGrain")]
        public async Task GetGuidGrain()
        {
            var g = this.GrainFactory.GetGrain<IGuidGrain>(Guid.NewGuid());
            Assert.True(await g.Foo());
        }
    }
}
