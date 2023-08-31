using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using Orleans.Hosting;
using Orleans.Metadata;

namespace UnitTests.General
{
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
                        services.AddSingleton<IConfigureGrainTypeComponents, HardcodedGrainActivator>();
                    });
                }
            }
        }

        public GrainActivatorTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("BVT")]
        public async Task CanUseCustomGrainActivatorToCreateGrains()
        {
            var grain = this.fixture.GrainFactory.GetGrain<ISimpleDIGrain>(GetRandomGrainId(), grainClassNamePrefix: "UnitTests.Grains.ExplicitlyRegistered");
            var actual = await grain.GetStringValue();
            Assert.Equal(HardcodedGrainActivator.HardcodedValue, actual);
        }

        [Fact, TestCategory("BVT")]
        public async Task CanUseCustomGrainActivatorToReleaseGrains()
        {
            var grain1 = this.fixture.GrainFactory.GetGrain<ISimpleDIGrain>(GetRandomGrainId(), grainClassNamePrefix: "UnitTests.Grains.ExplicitlyRegistered");
            var initialReleasedInstances = await grain1.GetLongValue();

            var grain2 = this.fixture.GrainFactory.GetGrain<ISimpleDIGrain>(GetRandomGrainId(), grainClassNamePrefix: "UnitTests.Grains.ExplicitlyRegistered");
            var secondReleasedInstances = await grain2.GetLongValue();

            Assert.Equal(initialReleasedInstances, secondReleasedInstances);

            await grain1.DoDeactivate();
            await Task.Delay(250);

            var grain3 = this.fixture.GrainFactory.GetGrain<ISimpleDIGrain>(GetRandomGrainId(), grainClassNamePrefix: "UnitTests.Grains.ExplicitlyRegistered");
            var finalReleasedInstances = await grain3.GetLongValue();
            Assert.Equal(initialReleasedInstances + 1, finalReleasedInstances);
        }

        private class HardcodedGrainActivator : IGrainActivator, IConfigureGrainTypeComponents
        {
            public const string HardcodedValue = "Hardcoded Test Value";
            private readonly GrainClassMap _grainClassMap;
            private int _released;

            public HardcodedGrainActivator(GrainClassMap grainClassMap)
            {
                _grainClassMap = grainClassMap;
            }

            public void Configure(GrainType grainType, GrainProperties properties, GrainTypeSharedContext shared)
            {
                if (_grainClassMap.TryGetGrainClass(grainType, out var grainClass) && grainClass.IsAssignableFrom(typeof(ExplicitlyRegisteredSimpleDIGrain)))
                {
                    shared.SetComponent<IGrainActivator>(this);
                }
            }

            public object CreateInstance(IGrainContext context)
            {
                return new ExplicitlyRegisteredSimpleDIGrain(new InjectedService(NullLoggerFactory.Instance), HardcodedValue, _released);
            }

            public ValueTask DisposeInstance(IGrainContext context, object instance)
            {
                ++_released;
                return default;
            }
        }
    }
}
