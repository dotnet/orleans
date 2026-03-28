using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using Orleans.Metadata;

namespace UnitTests.General
{
    /// <summary>
    /// Tests for custom grain activators, which allow complete control over grain instantiation and disposal.
    /// 
    /// Orleans uses IGrainActivator to create and destroy grain instances. By default, it uses
    /// dependency injection, but applications can provide custom activators for scenarios like:
    /// - Object pooling for expensive grain instances
    /// - Custom initialization logic that can't be done in constructors
    /// - Integration with third-party DI containers
    /// - Specialized cleanup during grain deactivation
    /// 
    /// This test demonstrates implementing a custom activator that bypasses DI entirely
    /// and tracks the number of disposed instances.
    /// </summary>
    [TestCategory("DI")]
    public class GrainActivatorTests : OrleansTestingBase, IClassFixture<GrainActivatorTests.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.InitialSilosCount = 1;
                builder.AddSiloBuilderConfigurator<TestSiloBuilderConfigurator>();
            }

            private class TestSiloBuilderConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder.ConfigureServices(services =>
                    {
                        // Register our custom grain activator as a grain type component configurator
                        // This allows it to selectively apply to specific grain types
                        services.AddSingleton<IConfigureGrainTypeComponents, HardcodedGrainActivator>();
                    });
                }
            }
        }

        public GrainActivatorTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        /// <summary>
        /// Verifies that custom grain activators can create grain instances without using DI.
        /// The custom activator injects a hardcoded value that proves it was used instead
        /// of the default DI-based activation.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task CanUseCustomGrainActivatorToCreateGrains()
        {
            ISimpleDIGrain grain = this.fixture.GrainFactory.GetGrain<ISimpleDIGrain>(GetRandomGrainId(), grainClassNamePrefix: "UnitTests.Grains.ExplicitlyRegistered");
            var actual = await grain.GetStringValue();
            Assert.Equal(HardcodedGrainActivator.HardcodedValue, actual);
        }

        /// <summary>
        /// Tests that custom grain activators receive disposal notifications when grains are deactivated.
        /// This is critical for resource cleanup scenarios like returning objects to pools,
        /// closing connections, or updating metrics. The test verifies the disposal count
        /// increases after explicit deactivation.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task CanUseCustomGrainActivatorToReleaseGrains()
        {
            ISimpleDIGrain grain1 = this.fixture.GrainFactory.GetGrain<ISimpleDIGrain>(GetRandomGrainId(), grainClassNamePrefix: "UnitTests.Grains.ExplicitlyRegistered");
            long initialReleasedInstances = await grain1.GetLongValue();

            ISimpleDIGrain grain2 = this.fixture.GrainFactory.GetGrain<ISimpleDIGrain>(GetRandomGrainId(), grainClassNamePrefix: "UnitTests.Grains.ExplicitlyRegistered");
            long secondReleasedInstances = await grain2.GetLongValue();

            Assert.Equal(initialReleasedInstances, secondReleasedInstances);

            await grain1.DoDeactivate();
            await Task.Delay(250);

            ISimpleDIGrain grain3 = this.fixture.GrainFactory.GetGrain<ISimpleDIGrain>(GetRandomGrainId(), grainClassNamePrefix: "UnitTests.Grains.ExplicitlyRegistered");
            long finalReleasedInstances = await grain3.GetLongValue();
            Assert.Equal(initialReleasedInstances + 1, finalReleasedInstances);
        }

        /// <summary>
        /// Custom grain activator that bypasses dependency injection entirely.
        /// Implements both IGrainActivator (for creation/disposal) and IConfigureGrainTypeComponents
        /// (to register itself for specific grain types). This demonstrates how to:
        /// - Create grains with custom logic instead of DI
        /// - Track lifecycle events like disposal
        /// - Selectively apply to specific grain types using the grain class map
        /// </summary>
        private class HardcodedGrainActivator : IGrainActivator, IConfigureGrainTypeComponents
        {
            public const string HardcodedValue = "Hardcoded Test Value";
            private readonly GrainClassMap _grainClassMap;
            private int _released;  // Tracks number of disposed grain instances

            public HardcodedGrainActivator(GrainClassMap grainClassMap)
            {
                _grainClassMap = grainClassMap;
            }

            public void Configure(GrainType grainType, GrainProperties properties, GrainTypeSharedContext shared)
            {
                // Selectively register this activator only for ExplicitlyRegisteredSimpleDIGrain types
                // Other grain types will continue using the default DI-based activator
                if (_grainClassMap.TryGetGrainClass(grainType, out var grainClass) && grainClass.IsAssignableFrom(typeof(ExplicitlyRegisteredSimpleDIGrain)))
                {
                    shared.SetComponent<IGrainActivator>(this);
                }
            }

            public object CreateInstance(IGrainContext context)
            {
                // Custom instantiation logic - creates grain with hardcoded dependencies
                // In real scenarios, this could get objects from a pool, perform complex
                // initialization, or integrate with external systems
                return new ExplicitlyRegisteredSimpleDIGrain(new InjectedService(NullLoggerFactory.Instance), HardcodedValue, _released);
            }

            public ValueTask DisposeInstance(IGrainContext context, object instance)
            {
                // Called when grain is deactivated - perfect for cleanup, returning to pools,
                // or updating metrics. The count allows tests to verify disposal happened.
                ++_released;
                return default;
            }
        }
    }
}
