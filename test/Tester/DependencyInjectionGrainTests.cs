using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
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
    [TestCategory("DI")]
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
            IDIGrainWithInjectedServices grain = GrainFactory.GetGrain<IDIGrainWithInjectedServices>(GetRandomGrainId());
            long ignored = await grain.GetTicksFromService();
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task CanGetGrainWithInjectedGrainFactory()
        {
            // please don't inject your implemetation of IGrainFactory to DI container in Startup Class, 
            // since we are currently not supporting replacing IGrainFactory 
            IDIGrainWithInjectedServices grain = GrainFactory.GetGrain<IDIGrainWithInjectedServices>(GetRandomGrainId());
            long ignored = await grain.GetGrainFactoryId();
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task CanResolveSingletonDependencies()
        {
            var grain1 = GrainFactory.GetGrain<IDIGrainWithInjectedServices>(GetRandomGrainId());
            var grain2 = GrainFactory.GetGrain<IDIGrainWithInjectedServices>(GetRandomGrainId());

            // the injected service will return the same value only if it's the same instance
            Assert.Equal(
                await grain1.GetStringValue(), 
                await grain2.GetStringValue());
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task CanResolveSingletonGrainFactory()
        {
            var grain1 = GrainFactory.GetGrain<IDIGrainWithInjectedServices>(GetRandomGrainId());
            var grain2 = GrainFactory.GetGrain<IDIGrainWithInjectedServices>(GetRandomGrainId());

            // the injected grain factory will return the same value only if it's the same instance,
            Assert.Equal(
                await grain1.GetGrainFactoryId(),
                await grain2.GetGrainFactoryId());
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

            // explicitly register a grain class to assert that it will NOT use the registration, 
            // as by design this is not supported.
            services.AddTransient<ExplicitlyRegisteredSimpleDIGrain>(
                sp => new ExplicitlyRegisteredSimpleDIGrain(
                    sp.GetRequiredService<IInjectedService>(),
                    "some value"));

            return services.BuildServiceProvider();
        }
    }
}
