using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using System.Collections.Generic;
using System.Linq;
using Orleans.Hosting;
using Orleans.TestingHost.Utils;

namespace UnitTests.General
{
    [TestCategory("DI")]
    public class DependencyInjectionGrainTests : OrleansTestingBase, IClassFixture<DependencyInjectionGrainTests.Fixture>
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
                        .ConfigureServices(ConfigureServices)
                        .ConfigureLogging(builder => TestingUtils.ConfigureDefaultLoggingBuilder(builder, clusterConfiguration.GetOrCreateNodeConfigurationForSilo(siloName).TraceFileName));
                }
            }

            private static void ConfigureServices(IServiceCollection services)
            {
                services.AddSingleton<IInjectedService, InjectedService>();
                services.AddScoped<IInjectedScopedService, InjectedScopedService>();

                // explicitly register a grain class to assert that it will NOT use the registration, 
                // as by design this is not supported.
                services.AddTransient<ExplicitlyRegisteredSimpleDIGrain>(
                    sp => new ExplicitlyRegisteredSimpleDIGrain(
                        sp.GetRequiredService<IInjectedService>(),
                        "some value",
                        5));
            }
        }

        public DependencyInjectionGrainTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task CanGetGrainWithInjectedDependencies()
        {
            IDIGrainWithInjectedServices grain = this.fixture.GrainFactory.GetGrain<IDIGrainWithInjectedServices>(GetRandomGrainId());
            long ignored = await grain.GetLongValue();
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task CanGetGrainWithInjectedGrainFactory()
        {
            // please don't inject your implemetation of IGrainFactory to DI container in Startup Class, 
            // since we are currently not supporting replacing IGrainFactory 
            IDIGrainWithInjectedServices grain = this.fixture.GrainFactory.GetGrain<IDIGrainWithInjectedServices>(GetRandomGrainId());
            long ignored = await grain.GetGrainFactoryId();
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task CanResolveSingletonDependencies()
        {
            var grain1 = this.fixture.GrainFactory.GetGrain<IDIGrainWithInjectedServices>(GetRandomGrainId());
            var grain2 = this.fixture.GrainFactory.GetGrain<IDIGrainWithInjectedServices>(GetRandomGrainId());

            // the injected service will return the same value only if it's the same instance
            Assert.Equal(
                await grain1.GetInjectedSingletonServiceValue(), 
                await grain2.GetInjectedSingletonServiceValue());

            await grain1.DoDeactivate();
            await grain2.DoDeactivate();
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task CanResolveScopedDependencies()
        {
            var grain1 = this.fixture.GrainFactory.GetGrain<IDIGrainWithInjectedServices>(GetRandomGrainId());
            var grain2 = this.fixture.GrainFactory.GetGrain<IDIGrainWithInjectedServices>(GetRandomGrainId());

            // the injected service will only return a different value if it's a different instance
            Assert.NotEqual(
                await grain1.GetInjectedScopedServiceValue(),
                await grain2.GetInjectedScopedServiceValue());

            await grain1.DoDeactivate();
            await grain2.DoDeactivate();
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task CanResolveScopedGrainActivationContext()
        {
            long id1 = GetRandomGrainId();
            long id2 = GetRandomGrainId();
            var grain1 = this.fixture.GrainFactory.GetGrain<IDIGrainWithInjectedServices>(id1);
            var grain2 = this.fixture.GrainFactory.GetGrain<IDIGrainWithInjectedServices>(id2);

            // the injected service will only return a different value if it's a different instance
            Assert.Contains(id1.ToString(), await grain1.GetStringValue());
            Assert.Contains(id2.ToString(), await grain2.GetStringValue());

            await grain1.DoDeactivate();
            await grain2.DoDeactivate();
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task ScopedDependenciesAreThreadSafe()
        {
            const int parallelCalls = 10;
            var grain1 = this.fixture.GrainFactory.GetGrain<IDIGrainWithInjectedServices>(GetRandomGrainId());

            var calls =
                Enumerable.Range(0, parallelCalls)
                    .Select(i => grain1.GetInjectedScopedServiceValue())
                    .ToList();

            await Task.WhenAll(calls);
            string expected = calls[0].Result;
            foreach (var value in calls.Select(x => x.Result))
            {
                Assert.Equal(expected, value);
            }

            await grain1.DoDeactivate();
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task CanResolveSameDependenciesViaServiceProvider()
        {
            var grain1 = this.fixture.GrainFactory.GetGrain<IDIGrainWithInjectedServices>(GetRandomGrainId());
            var grain2 = this.fixture.GrainFactory.GetGrain<IDIGrainWithInjectedServices>(GetRandomGrainId());

            await grain1.AssertCanResolveSameServiceInstances();
            await grain2.AssertCanResolveSameServiceInstances();

            await grain1.DoDeactivate();
            await grain2.DoDeactivate();
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task CanResolveSingletonGrainFactory()
        {
            var grain1 = this.fixture.GrainFactory.GetGrain<IDIGrainWithInjectedServices>(GetRandomGrainId());
            var grain2 = this.fixture.GrainFactory.GetGrain<IDIGrainWithInjectedServices>(GetRandomGrainId());

            // the injected grain factory will return the same value only if it's the same instance,
            Assert.Equal(
                await grain1.GetGrainFactoryId(),
                await grain2.GetGrainFactoryId());
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task CannotGetExplictlyRegisteredGrain()
        {
            ISimpleDIGrain grain = this.fixture.GrainFactory.GetGrain<ISimpleDIGrain>(GetRandomGrainId(), grainClassNamePrefix: "UnitTests.Grains.ExplicitlyRegistered");
            var exception = await Assert.ThrowsAsync<OrleansException>(() => grain.GetLongValue());
            Assert.Contains("Error creating activation for", exception.Message);
            Assert.Contains(nameof(ExplicitlyRegisteredSimpleDIGrain), exception.Message);
        }
    }
}
