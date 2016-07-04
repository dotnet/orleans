using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using UnitTests.Tester;
using Tester;
using Xunit;

namespace UnitTests.General
{
    public class DependencyInjectionGrainTests : OrleansTestingBase, IClassFixture<DependencyInjectionGrainTests.Fixture>
    {
        private class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(1);
                options.ClusterConfiguration.ApplyToAllNodes(nodeConfig => nodeConfig.StartupTypeName = typeof(TestStartup).AssemblyQualifiedName);
                return new TestCluster(options);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task CanGetGrainWithInjectedDependencies()
        {
            ISimpleDIGrain grain = GrainFactory.GetGrain<ISimpleDIGrain>(GetRandomGrainId());
            long ignored = await grain.GetTicksFromService();
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task CanResolveSingletonDependencies()
        {
            var grain1 = GrainFactory.GetGrain<ISimpleDIGrain>(GetRandomGrainId());
            var grain2 = GrainFactory.GetGrain<ISimpleDIGrain>(GetRandomGrainId());

            // the injected service will return the same value only if it's the same instance
            Assert.Equal(
                await grain1.GetStringValue(), 
                await grain2.GetStringValue());
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task CanGetExplictlyRegisteredGrain()
        {
            ISimpleDIGrain grain = GrainFactory.GetGrain<ISimpleDIGrain>(GetRandomGrainId(), grainClassNamePrefix: "UnitTests.Grains.ExplicitlyRegistered");
            long ignored = await grain.GetTicksFromService();
            Assert.Equal(TestStartup.ExplicitlyRegistrationValue, await grain.GetStringValue());
        }
    }

    public class TestStartup
    {
        public const string ExplicitlyRegistrationValue = "explict registration value";
        public IServiceProvider ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<IInjectedService, InjectedService>();

            services.AddTransient<ExplicitlyRegisteredSimpleDIGrain>(
                sp => new ExplicitlyRegisteredSimpleDIGrain(
                    sp.GetRequiredService<IInjectedService>(),
                    ExplicitlyRegistrationValue));

            return services.BuildServiceProvider();
        }
    }
}
