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
using Orleans.Hosting;

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
                options.UseSiloBuilderFactory<TestSiloBuilderFactory>();
                return new TestCluster(options);
            }

            private class TestSiloBuilderFactory : ISiloBuilderFactory
            {
                public ISiloBuilder CreateSiloBuilder(string siloName, ClusterConfiguration clusterConfiguration)
                {
                    return new SiloBuilder()
                        .ConfigureSiloName(siloName)
                        .UseConfiguration(clusterConfiguration)
                        .ConfigureServices(ConfigureServices);
                }
            }

            private static void ConfigureServices(IServiceCollection services)
            {
                services.Replace(ServiceDescriptor.Singleton(typeof(IGrainActivator), typeof(HardcodedGrainActivator)));
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
            Assert.Equal(HardcodedGrainActivator.HardcodedValue, actual);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
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

        private class HardcodedGrainActivator : DefaultGrainActivator, IGrainActivator
        {
            public const string HardcodedValue = "Hardcoded Test Value";
            private int numberOfReleasedInstances;

            public HardcodedGrainActivator(IServiceProvider service) : base(service)
            {
            }

            public override object Create(IGrainActivationContext context)
            {
                if (context.GrainType == typeof(ExplicitlyRegisteredSimpleDIGrain))
                {
                    return new ExplicitlyRegisteredSimpleDIGrain(new InjectedService(null), HardcodedValue, numberOfReleasedInstances);
                }

                return base.Create(context);
            }

            public override void Release(IGrainActivationContext context, object grain)
            {
                if (context.GrainType == typeof(ExplicitlyRegisteredSimpleDIGrain))
                {
                    numberOfReleasedInstances++;
                }
                else
                {
                    base.Release(context, grain);
                }
            }
        }
    }
}
