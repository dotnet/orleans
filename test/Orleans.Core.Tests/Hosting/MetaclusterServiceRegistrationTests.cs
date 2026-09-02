using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using Orleans.Serialization.Invocation;
using TestExtensions;
using Xunit;

namespace UnitTests.Hosting;

[TestArea("Hosting")]
[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[Trait("Phase", "4")]
[Trait("FullyQualifiedName", "UnitTests.Hosting.MetaclusterServiceRegistrationTests")]
public sealed class MetaclusterServiceRegistrationTests
{
    [Fact]
    public void DefaultClientServices_RegisterFailClosedInterClusterTransport()
    {
        var services = CreateClientServices();
        using var provider = services.BuildServiceProvider();

        var transport = provider.GetRequiredService<IInterClusterTransport>();

        Assert.IsType<UnavailableInterClusterTransport>(transport);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IInterClusterTransport));
    }

    [Fact]
    public void DefaultSiloServices_RegisterRejectingAuthorizer()
    {
        var services = CreateSiloServices();
        using var provider = services.BuildServiceProvider();

        var authorizer = provider.GetRequiredService<IInterClusterRequestAuthorizer>();

        Assert.IsType<RejectingInterClusterRequestAuthorizer>(authorizer);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IInterClusterRequestAuthorizer));
    }

    [Fact]
    public void DefaultServices_RegisterTopologyLocatorResolverAndReferenceResolver()
    {
        var client = CreateClientServices();
        var silo = CreateSiloServices();
        var required = new[]
        {
            typeof(IMetaclusterTopologyProvider),
            typeof(ClusterLocatorResolver),
            typeof(ClusterReferenceResolver),
            typeof(ClusterPlacementStrategyResolver),
            typeof(ClusterPlacementDirectorResolver)
        };

        foreach (var serviceType in required)
        {
            Assert.Single(client, descriptor => descriptor.ServiceType == serviceType);
            Assert.Single(silo, descriptor => descriptor.ServiceType == serviceType);
        }
    }

    [Fact]
    public void AddClusterLocator_RegistersKeyedLocatorWithoutReplacingOtherKeys()
    {
        var services = new ServiceCollection();
        services.AddClusterLocator<EastLocator>("east");
        services.AddClusterLocator<WestLocator>("west");
        using var provider = services.BuildServiceProvider();

        var east = provider.GetRequiredKeyedService<IClusterLocator>("east");
        var west = provider.GetRequiredKeyedService<IClusterLocator>("west");

        Assert.IsType<EastLocator>(east);
        Assert.IsType<WestLocator>(west);
        Assert.NotSame(east, west);
    }

    [Fact]
    public void AddClusterPlacementDirector_RegistersKeyedDirectorWithoutReplacingOtherKeys()
    {
        var services = new ServiceCollection();
        services.AddClusterPlacement<EastStrategy, EastDirector>();
        services.AddClusterPlacement<WestStrategy, WestDirector>();
        using var provider = services.BuildServiceProvider();

        var eastStrategy = provider.GetRequiredKeyedService<ClusterPlacementStrategy>(nameof(EastStrategy));
        var westStrategy = provider.GetRequiredKeyedService<ClusterPlacementStrategy>(nameof(WestStrategy));
        var eastDirector = provider.GetRequiredKeyedService<IClusterPlacementDirector>(typeof(EastStrategy));
        var westDirector = provider.GetRequiredKeyedService<IClusterPlacementDirector>(typeof(WestStrategy));

        Assert.IsType<EastStrategy>(eastStrategy);
        Assert.IsType<WestStrategy>(westStrategy);
        Assert.IsType<EastDirector>(eastDirector);
        Assert.IsType<WestDirector>(westDirector);
    }

    [Fact]
    public void ClientAndSiloRegistrations_IsolateSameNameAcrossServiceKinds()
    {
        var client = new TestClientBuilder();
        var silo = new TestSiloBuilder();
        client.AddClusterLocator<EastLocator>("shared");
        client.AddClusterPlacement<EastStrategy, EastDirector>();
        silo.AddClusterLocator<WestLocator>("shared");
        silo.AddClusterPlacement<WestStrategy, WestDirector>();
        using var clientProvider = client.Services.BuildServiceProvider();
        using var siloProvider = silo.Services.BuildServiceProvider();

        Assert.IsType<EastLocator>(clientProvider.GetRequiredKeyedService<IClusterLocator>("shared"));
        Assert.IsType<WestLocator>(siloProvider.GetRequiredKeyedService<IClusterLocator>("shared"));
        Assert.IsType<EastDirector>(
            clientProvider.GetRequiredKeyedService<IClusterPlacementDirector>(typeof(EastStrategy)));
        Assert.IsType<WestDirector>(
            siloProvider.GetRequiredKeyedService<IClusterPlacementDirector>(typeof(WestStrategy)));
    }

    [Fact]
    public void ApplicationRegistration_ReplacesOnlyIntendedDefault()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IInterClusterTransport, ApplicationTransport>();
        _ = new ClientBuilder(services, new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        Assert.IsType<ApplicationTransport>(provider.GetRequiredService<IInterClusterTransport>());
        Assert.IsType<StaticMetaclusterTopologyProvider>(provider.GetRequiredService<IMetaclusterTopologyProvider>());
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IInterClusterTransport));
    }

    [Fact]
    public void RepeatedRegistration_HasDefinedIdempotentResolution()
    {
        var services = new ServiceCollection();
        var builder = new ClientBuilder(services, new ConfigurationBuilder().Build());
        var before = services.Count;
        var resolverCount = services.Count(descriptor => descriptor.ServiceType == typeof(ClusterReferenceResolver));

        DefaultClientServices.AddDefaultServices(builder);

        Assert.Equal(before, services.Count);
        Assert.Equal(resolverCount, services.Count(descriptor => descriptor.ServiceType == typeof(ClusterReferenceResolver)));
        Assert.Equal(1, resolverCount);
    }

    [Fact]
    public void SiloBuilderExtension_ValidatesMetaclusterOptionsAtStartup()
    {
        var services = new ServiceCollection();
        var builder = new SiloBuilder(services, new ConfigurationBuilder().Build());
        builder.UseMetacluster(options =>
        {
            options.ClusterOwnershipLeaseDuration = TimeSpan.FromSeconds(10);
            options.ClusterOwnershipLeaseRenewalWindow = TimeSpan.FromSeconds(10);
        });
        using var provider = services.BuildServiceProvider();

        var validator = provider.GetServices<IConfigurationValidator>().OfType<MetaclusterOptionsValidator>().Single();
        var exception = Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);

        Assert.Contains(nameof(MetaclusterOptions.ClusterOwnershipLeaseRenewalWindow), exception.Message, StringComparison.Ordinal);
        Assert.True(provider.GetRequiredService<IOptions<MetaclusterOptions>>().Value.Enabled);
    }

    [Fact]
    public void ClientBuilderExtension_ValidatesMetaclusterOptionsAtStartup()
    {
        var services = new ServiceCollection();
        var builder = new ClientBuilder(services, new ConfigurationBuilder().Build());
        builder.UseMetacluster(options => options.ClusterLocationCacheDuration = TimeSpan.FromSeconds(-1));
        using var provider = services.BuildServiceProvider();

        var validator = provider.GetServices<IConfigurationValidator>().OfType<MetaclusterOptionsValidator>().Single();
        var exception = Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);

        Assert.Contains(nameof(MetaclusterOptions.ClusterLocationCacheDuration), exception.Message, StringComparison.Ordinal);
        Assert.True(provider.GetRequiredService<IOptions<MetaclusterOptions>>().Value.Enabled);
    }

    private static ServiceCollection CreateClientServices()
    {
        var services = new ServiceCollection();
        _ = new ClientBuilder(services, new ConfigurationBuilder().Build());
        return services;
    }

    private static ServiceCollection CreateSiloServices()
    {
        var services = new ServiceCollection();
        _ = new SiloBuilder(services, new ConfigurationBuilder().Build());
        return services;
    }

    private sealed class TestClientBuilder : IClientBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    private sealed class EastLocator : IClusterLocator
    {
        public ValueTask<ClusterLocation> Locate(
            GrainId grainId,
            ClusterLocationContext context,
            CancellationToken cancellationToken = default)
            => new(new ClusterLocation("east", 1, 1, false));
    }

    private sealed class WestLocator : IClusterLocator
    {
        public ValueTask<ClusterLocation> Locate(
            GrainId grainId,
            ClusterLocationContext context,
            CancellationToken cancellationToken = default)
            => new(new ClusterLocation("west", 1, 1, false));
    }

    private sealed class EastStrategy : ClusterPlacementStrategy;

    private sealed class WestStrategy : ClusterPlacementStrategy;

    private sealed class EastDirector : IClusterPlacementDirector
    {
        public ValueTask<ClusterPlacementResult> SelectClusters(
            ClusterPlacementStrategy strategy,
            GrainId grainId,
            ClusterLocationContext context,
            CancellationToken cancellationToken = default)
            => new(new ClusterPlacementResult(["east"]));
    }

    private sealed class WestDirector : IClusterPlacementDirector
    {
        public ValueTask<ClusterPlacementResult> SelectClusters(
            ClusterPlacementStrategy strategy,
            GrainId grainId,
            ClusterLocationContext context,
            CancellationToken cancellationToken = default)
            => new(new ClusterPlacementResult(["west"]));
    }

    private sealed class ApplicationTransport : IInterClusterTransport
    {
        public ValueTask<Response> SendRequest(
            ClusterIdentity destination,
            UniversalReference target,
            IInvokable request,
            Orleans.CodeGeneration.InvokeMethodOptions options,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<Response>(new InvalidOperationException("Not used by registration tests."));
    }
}
