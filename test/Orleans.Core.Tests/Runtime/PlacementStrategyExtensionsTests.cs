using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using Xunit;

namespace NonSilo.Tests.Runtime;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Placement")]
public sealed class PlacementStrategyExtensionsTests
{
    [Fact]
    public void TypedDirectorRejectsNullServiceCollection()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            ((IServiceCollection)null!).AddPlacementDirector<RandomPlacement, TestDirector>(ServiceLifetime.Singleton));

        Assert.Equal("services", exception.ParamName);
    }

    [Fact]
    public void FactoryDirectorRejectsNullServiceCollection()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            ((IServiceCollection)null!).AddPlacementDirector<RandomPlacement>(_ => new TestDirector(), ServiceLifetime.Singleton));

        Assert.Equal("services", exception.ParamName);
    }

    [Fact]
    public void NullDirectorFactoryPreservesRegistrations()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            services.AddPlacementDirector<RandomPlacement>(null!, ServiceLifetime.Singleton));

        Assert.Equal("createDirector", exception.ParamName);
        Assert.Empty(services);
    }

    [Theory]
    [InlineData(ServiceLifetime.Singleton)]
    [InlineData(ServiceLifetime.Scoped)]
    [InlineData(ServiceLifetime.Transient)]
    public void TypedRegistrationPreservesStrategyLifetimeAndKey(ServiceLifetime lifetime)
    {
        var services = new ServiceCollection();

        services.AddPlacementDirector<RandomPlacement, TestDirector>(lifetime);
        using var provider = services.BuildServiceProvider();

        var strategy = Assert.Single(services, registration => registration.ServiceType == typeof(PlacementStrategy));
        Assert.Equal(nameof(RandomPlacement), strategy.ServiceKey);
        Assert.Equal(lifetime, strategy.Lifetime);
        var first = provider.GetRequiredKeyedService<IPlacementDirector>(typeof(RandomPlacement));
        var second = provider.GetRequiredKeyedService<IPlacementDirector>(typeof(RandomPlacement));
        Assert.IsType<TestDirector>(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void FactoryRegistrationPreservesProviderAndSingletonDirector()
    {
        var services = new ServiceCollection();
        var director = new TestDirector();
        IServiceProvider factoryProvider = null!;
        var calls = 0;
        services.AddPlacementDirector<RandomPlacement>(provider =>
        {
            factoryProvider = provider;
            calls++;
            return director;
        });
        using var provider = services.BuildServiceProvider();

        Assert.Same(director, provider.GetRequiredKeyedService<IPlacementDirector>(typeof(RandomPlacement)));
        Assert.Same(director, provider.GetRequiredKeyedService<IPlacementDirector>(typeof(RandomPlacement)));
        Assert.Same(provider.GetRequiredService<IServiceProvider>(), factoryProvider);
        Assert.Equal(1, calls);
    }

    private sealed class TestDirector : IPlacementDirector
    {
        public TestDirector() { }

        public Task<SiloAddress> OnAddActivation(PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
            => Task.FromResult(SiloAddress.Zero);
    }
}
