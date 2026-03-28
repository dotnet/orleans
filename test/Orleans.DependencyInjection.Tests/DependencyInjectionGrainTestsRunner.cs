using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace DependencyInjection.Tests
{
    /// <summary>
    /// Test runner for Orleans dependency injection integration tests.
    /// 
    /// Orleans integrates with Microsoft.Extensions.DependencyInjection to support:
    /// - Constructor injection in grains
    /// - Scoped services (one instance per grain activation)
    /// - Singleton services (shared across all grains)
    /// - Integration with third-party DI containers
    /// 
    /// Key concepts tested:
    /// - Grain constructors can have dependencies injected
    /// - Each grain activation gets its own scope
    /// - IGrainFactory and IGrainActivationContext are automatically available
    /// - Generic grains can have dependencies based on their type parameters
    /// - Explicit grain registrations in DI are NOT supported by design
    /// 
    /// This abstract base class contains the core test logic, while concrete
    /// implementations test different DI container integrations.
    /// </summary>
    public abstract class DependencyInjectionGrainTestRunner : OrleansTestingBase
    {
        private readonly BaseTestClusterFixture fixture;

        /// <summary>
        /// Configures services for dependency injection tests.
        /// This is in the base test runner because all DI tests need these services,
        /// while different ServiceProviderFactory setups are in concrete test classes.
        /// </summary>
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

                    // Explicitly register a grain class to test that it will NOT use this registration.
                    // Orleans creates grains through its own activation system, not DI container.
                    // This registration is here to verify that Orleans ignores it and fails
                    // when trying to create the grain (missing constructor parameters).
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

        /// <summary>
        /// Basic test that grains can be activated with constructor dependencies.
        /// This verifies the fundamental DI integration is working.
        /// </summary>
        [Fact]
        public async Task CanGetGrainWithInjectedDependencies()
        {
            IDIGrainWithInjectedServices grain = this.fixture.GrainFactory.GetGrain<IDIGrainWithInjectedServices>(GetRandomGrainId());
            var _ = await grain.GetLongValue();
        }

        /// <summary>
        /// Tests that IGrainFactory is automatically available for injection.
        /// IGrainFactory is a framework service that allows grains to create other grains.
        /// Note: Don't register your own IGrainFactory - Orleans provides this automatically.
        /// </summary>
        [Fact]
        public async Task CanGetGrainWithInjectedGrainFactory()
        {
            // please don't inject your implementation of IGrainFactory to DI container in Startup Class, 
            // since we are currently not supporting replacing IGrainFactory 
            IDIGrainWithInjectedServices grain = this.fixture.GrainFactory.GetGrain<IDIGrainWithInjectedServices>(GetRandomGrainId());
            _ = await grain.GetGrainFactoryId();
        }

        /// <summary>
        /// Verifies that singleton services are shared across all grain activations.
        /// The same instance should be injected into different grains.
        /// </summary>
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

        /// <summary>
        /// Verifies that scoped services create unique instances per grain activation.
        /// Each grain should get its own instance of scoped services.
        /// This is crucial for services that maintain per-grain state.
        /// </summary>
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

        /// <summary>
        /// Tests that IGrainActivationContext is available as a scoped service.
        /// Each grain activation has its own context with grain-specific information
        /// like GrainId, which should be accessible through DI.
        /// </summary>
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

        /// <summary>
        /// Verifies that scoped services are thread-safe within a grain activation.
        /// Multiple concurrent calls to the same grain should use the same
        /// scoped service instances, as grains process requests sequentially.
        /// </summary>
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
            string expected = await calls[0];
            foreach (var callTask in calls)
            {
                Assert.Equal(expected, await callTask);
            }

            await grain1.DoDeactivate();
        }

        /// <summary>
        /// Tests that grains can access the DI container through IServiceProvider.
        /// Services resolved directly from the provider should match those
        /// injected through the constructor for the same grain activation.
        /// </summary>
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

        /// <summary>
        /// Verifies that IGrainFactory is registered as a singleton.
        /// All grains in the silo should receive the same IGrainFactory instance.
        /// </summary>
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

        /// <summary>
        /// Tests that explicitly registered grain classes in DI are ignored.
        /// Orleans must create grains through its activation system, not DI,
        /// to ensure proper lifecycle management and distributed behavior.
        /// This test verifies that the explicit registration is not used.
        /// </summary>
        [Fact]
        public async Task CannotGetExplictlyRegisteredGrain()
        {
            ISimpleDIGrain grain = this.fixture.GrainFactory.GetGrain<ISimpleDIGrain>(GetRandomGrainId(), grainClassNamePrefix: "UnitTests.Grains.ExplicitlyRegistered");
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => grain.GetLongValue());
            var innerException = Assert.IsType<InvalidOperationException>(exception.InnerException);
            Assert.Contains("Unable to resolve service for type 'System.String' while attempting to activate 'UnitTests.Grains.ExplicitlyRegisteredSimpleDIGrain'", innerException.Message);
        }

        /// <summary>
        /// Tests that generic grains can have dependencies injected based on their type parameters.
        /// For example, IReducer<TState, TAction> can be resolved differently for different
        /// TState/TAction combinations, allowing type-safe dependency injection.
        /// </summary>
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
