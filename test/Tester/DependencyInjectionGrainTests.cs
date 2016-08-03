using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Tester;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using UnitTests.Tester;
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
        public async Task CannotGetExplictlyRegisteredGrain()
        {
            ISimpleDIGrain grain = GrainFactory.GetGrain<ISimpleDIGrain>(GetRandomGrainId(), grainClassNamePrefix: "UnitTests.Grains.ExplicitlyRegistered");
            var exception = await Assert.ThrowsAsync<OrleansException>(() => grain.GetTicksFromService());
            Assert.Contains("Error creating activation for", exception.Message);
            Assert.Contains(nameof(ExplicitlyRegisteredSimpleDIGrain), exception.Message);
        }
    }

    public class TestStartup
    {
        public IServiceProvider ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<IInjectedService, InjectedService>();

            services.AddTransient<ExplicitlyRegisteredSimpleDIGrain>(
                sp => new ExplicitlyRegisteredSimpleDIGrain(
                    sp.GetRequiredService<IInjectedService>(),
                    "some value"));

            return services.BuildServiceProvider();
        }
    }
}
