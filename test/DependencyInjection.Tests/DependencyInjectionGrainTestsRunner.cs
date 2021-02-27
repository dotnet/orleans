using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using System.Linq;
using Orleans.Hosting;
using System;

namespace DependencyInjection.Tests
{
    public abstract class DependencyInjectionGrainTestRunner : OrleansTestingBase
    {
        private readonly BaseTestClusterFixture fixture;

        //contains IServiceCollection configuration for the following tests, so should be part of the test runner.
        //while different ServiceProviderFactory set up should be in the more concrete test files
        protected class TestSiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.ConfigureServices(services =>
                {
                    services.AddSingleton<IReducer<string, Reducer1Action>>(x => new Reducer1());
                    services.AddSingleton<IReducer<int, Reducer2Action>>(x => new Reducer2());
                    services.AddSingleton<IInjectedService, InjectedService>();
                    services.AddScoped<IInjectedScopedService, InjectedScopedService>();

                    // explicitly register a grain class to assert that it will NOT use the registration, 
                    // as by design this is not supported.
                    services.AddTransient(
                        sp => new ExplicitlyRegisteredSimpleDIGrain(
                            sp.GetRequiredService<IInjectedService>(),
                            "some value",
                            5));
                });
            }
        }

        public DependencyInjectionGrainTestRunner(BaseTestClusterFixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact]
        public async Task CanGetGrainWithInjectedDependencies()
        {
            IDIGrainWithInjectedServices grain = this.fixture.GrainFactory.GetGrain<IDIGrainWithInjectedServices>(GetRandomGrainId());
            var _ = await grain.GetLongValue();
        }

        [Fact]
        public async Task CanGetGrainWithInjectedGrainFactory()
        {
            // please don't inject your implemetation of IGrainFactory to DI container in Startup Class, 
            // since we are currently not supporting replacing IGrainFactory 
            IDIGrainWithInjectedServices grain = this.fixture.GrainFactory.GetGrain<IDIGrainWithInjectedServices>(GetRandomGrainId());
            _ = await grain.GetGrainFactoryId();
        }

        [Fact]
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

        [Fact]
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

        [Fact]
        public async Task CanResolveScopedGrainActivationContext()
        {
            long id1 = GetRandomGrainId();
            long id2 = GetRandomGrainId();
            var grain1 = this.fixture.GrainFactory.GetGrain<IDIGrainWithInjectedServices>(id1);
            var grain2 = this.fixture.GrainFactory.GetGrain<IDIGrainWithInjectedServices>(id2);

            // the injected service will only return a different value if it's a different instance
            Assert.Contains(id1.ToString("X"), await grain1.GetStringValue());
            Assert.Contains(id2.ToString("X"), await grain2.GetStringValue());

            await grain1.DoDeactivate();
            await grain2.DoDeactivate();
        }

        [Fact]
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

        [Fact]
        public async Task CanResolveSameDependenciesViaServiceProvider()
        {
            var grain1 = this.fixture.GrainFactory.GetGrain<IDIGrainWithInjectedServices>(GetRandomGrainId());
            var grain2 = this.fixture.GrainFactory.GetGrain<IDIGrainWithInjectedServices>(GetRandomGrainId());

            await grain1.AssertCanResolveSameServiceInstances();
            await grain2.AssertCanResolveSameServiceInstances();

            await grain1.DoDeactivate();
            await grain2.DoDeactivate();
        }

        [Fact]
        public async Task CanResolveSingletonGrainFactory()
        {
            var grain1 = this.fixture.GrainFactory.GetGrain<IDIGrainWithInjectedServices>(GetRandomGrainId());
            var grain2 = this.fixture.GrainFactory.GetGrain<IDIGrainWithInjectedServices>(GetRandomGrainId());

            // the injected grain factory will return the same value only if it's the same instance,
            Assert.Equal(
                await grain1.GetGrainFactoryId(),
                await grain2.GetGrainFactoryId());
        }

        [Fact]
        public async Task CannotGetExplictlyRegisteredGrain()
        {
            ISimpleDIGrain grain = this.fixture.GrainFactory.GetGrain<ISimpleDIGrain>(GetRandomGrainId(), grainClassNamePrefix: "UnitTests.Grains.ExplicitlyRegistered");
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => grain.GetLongValue());
            Assert.Contains("Unable to resolve service for type 'System.String' while attempting to activate 'UnitTests.Grains.ExplicitlyRegisteredSimpleDIGrain'", exception.Message);
        }

        [Fact]
        public async Task CanUseGenericArgumentsInConstructor()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IReducerGameGrain<string, Reducer1Action>>("reducer1");
            Assert.NotNull(await grain.Go("378", new Reducer1Action()));
            var grain2 = this.fixture.GrainFactory.GetGrain<IReducerGameGrain<int, Reducer2Action>>("reducer1");
            Assert.NotEqual(0, await grain2.Go(378, new Reducer2Action()));
        }
    }
}
