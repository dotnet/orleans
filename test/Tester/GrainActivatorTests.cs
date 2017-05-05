using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace UnitTests.General
{
    [TestCategory("DI")]
    public class GrainActivatorTests : OrleansTestingBase, IClassFixture<GrainActivatorTests.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(1);
                options.ClusterConfiguration.UseStartupType<TestStartup>();
                return new TestCluster(options);
            }
        }

        public GrainActivatorTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task CanUseCustomGrainActivatorToCreateGrains()
        {
            ISimpleDIGrain grain = this.fixture.GrainFactory.GetGrain<ISimpleDIGrain>(GetRandomGrainId(), grainClassNamePrefix: "UnitTests.Grains.ExplicitlyRegistered");
            var actual = await grain.GetStringValue();
            Assert.Equal(TestStartup.HardcodedGrainActivator.HardcodedValue, actual);
        }

        public class TestStartup
        {
            public class HardcodedGrainActivator : DefaultGrainActivator, IGrainActivator
            {
                public const string HardcodedValue = "Hardcoded Test Value";

                public override object Create(IGrainActivationContext context)
                {
                    if (context.GrainType == typeof(ExplicitlyRegisteredSimpleDIGrain))
                    {
                        return new ExplicitlyRegisteredSimpleDIGrain(new InjectedService(), HardcodedValue, 0);
                    }

                    return base.Create(context);
                }
            }

            public IServiceProvider ConfigureServices(IServiceCollection services)
            {
                services.Replace(ServiceDescriptor.Singleton(typeof(IGrainActivator), typeof(HardcodedGrainActivator)));

                return services.BuildServiceProvider();
            }
        }
    }
}
