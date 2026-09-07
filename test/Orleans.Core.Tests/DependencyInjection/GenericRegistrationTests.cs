using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using TestExtensions;
using Xunit;

namespace UnitTests.DependencyInjection;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
[TestCategory("BVT")]
public class CoreGenericRegistrationTests
{
    [Fact]
    public void PlacementFilter_ActivatesConstructorInjectedDirector()
    {
        var dependency = new ActivationMarker();
        var services = new ServiceCollection();
        services.AddSingleton(dependency);
        services.AddPlacementFilter<TestPlacementFilterStrategy, ConstructorInjectedPlacementFilterDirector>(
            ServiceLifetime.Singleton);

        using var provider = services.BuildServiceProvider();
        var strategy = Assert.IsType<TestPlacementFilterStrategy>(
            provider.GetRequiredKeyedService<PlacementFilterStrategy>(nameof(TestPlacementFilterStrategy)));
        var director = Assert.IsType<ConstructorInjectedPlacementFilterDirector>(
            provider.GetRequiredKeyedService<IPlacementFilterDirector>(typeof(TestPlacementFilterStrategy)));

        Assert.Equal(17, strategy.Order);
        Assert.Same(dependency, director.Dependency);
    }

    [Fact]
    public void OptionFormatter_ActivatesConstructorInjectedFormatterAndResolver()
    {
        var dependency = new ActivationMarker();
        var services = new ServiceCollection();
        services.AddSingleton(dependency);
        services.TryConfigureFormatter<TestOptions, ConstructorInjectedOptionFormatter>();
        services.TryConfigureFormatterResolver<TestOptions, ConstructorInjectedOptionFormatterResolver>();

        using var provider = services.BuildServiceProvider();
        var formatter = Assert.IsType<ConstructorInjectedOptionFormatter>(
            provider.GetRequiredService<IOptionFormatter<TestOptions>>());
        var resolver = Assert.IsType<ConstructorInjectedOptionFormatterResolver>(
            provider.GetRequiredService<IOptionFormatterResolver<TestOptions>>());

        Assert.Same(dependency, formatter.Dependency);
        Assert.Same(dependency, resolver.Dependency);
    }

    [Fact]
    public void ClientBuilder_ActivatesConstructorInjectedRetryFilterAndObserver()
    {
        var dependency = new ActivationMarker();
        var builder = new TestClientBuilder();
        builder.Services.AddSingleton(dependency);
        builder.UseConnectionRetryFilter<ConstructorInjectedConnectionRetryFilter>();
        builder.AddClusterConnectionStatusObserver<ConstructorInjectedClusterConnectionStatusObserver>();

        using var provider = builder.Services.BuildServiceProvider();
        var retryFilter = Assert.IsType<ConstructorInjectedConnectionRetryFilter>(
            provider.GetRequiredService<IClientConnectionRetryFilter>());
        var observer = Assert.IsType<ConstructorInjectedClusterConnectionStatusObserver>(
            provider.GetRequiredService<IClusterConnectionStatusObserver>());

        Assert.Same(dependency, retryFilter.Dependency);
        Assert.Same(dependency, observer.Dependency);
    }

    [Fact]
    public void ConfigureServices_NullBuilder_ThrowsBeforeDelegateInvocation()
    {
        var invoked = false;
        IClientBuilder builder = null!;

        var exception = Assert.Throws<ArgumentNullException>(
            () => builder.ConfigureServices(_ => invoked = true));

        Assert.Equal("builder", exception.ParamName);
        Assert.False(invoked);
    }

    [Fact]
    public void AddActivityPropagation_NullBuilder_ThrowsWithExactParameterName()
    {
        IClientBuilder builder = null!;

        var exception = Assert.Throws<ArgumentNullException>(() => builder.AddActivityPropagation());

        Assert.Equal("builder", exception.ParamName);
    }

    [Fact]
    public void AddClusterConnectionStatusObserver_NullObserver_ThrowsWithoutMutation()
    {
        var builder = new TestClientBuilder();
        var descriptorCount = builder.Services.Count;

        var exception = Assert.Throws<ArgumentNullException>(
            () => builder.AddClusterConnectionStatusObserver<IClusterConnectionStatusObserver>(null!));

        Assert.Equal("observer", exception.ParamName);
        Assert.Equal(descriptorCount, builder.Services.Count);
    }

    [Fact]
    public void GrainCallFilters_ActivateConstructorInjectedImplementations()
    {
        var dependency = new ActivationMarker();
        var services = new ServiceCollection();
        services.AddSingleton(dependency);
        services.AddIncomingGrainCallFilter<ConstructorInjectedIncomingGrainCallFilter>();
        services.AddOutgoingGrainCallFilter<ConstructorInjectedOutgoingGrainCallFilter>();

        using var provider = services.BuildServiceProvider();
        var incoming = Assert.IsType<ConstructorInjectedIncomingGrainCallFilter>(
            provider.GetRequiredService<IIncomingGrainCallFilter>());
        var outgoing = Assert.IsType<ConstructorInjectedOutgoingGrainCallFilter>(
            provider.GetRequiredService<IOutgoingGrainCallFilter>());

        Assert.Same(dependency, incoming.Dependency);
        Assert.Same(dependency, outgoing.Dependency);
    }

    private sealed class ActivationMarker;

    private sealed class TestPlacementFilterStrategy : PlacementFilterStrategy
    {
        public TestPlacementFilterStrategy()
            : base(17)
        {
        }
    }

    private sealed class ConstructorInjectedPlacementFilterDirector : IPlacementFilterDirector
    {
        public ConstructorInjectedPlacementFilterDirector(ActivationMarker dependency)
        {
            Dependency = dependency;
        }

        public ActivationMarker Dependency { get; }

        public IEnumerable<SiloAddress> Filter(
            PlacementFilterStrategy filterStrategy,
            PlacementTarget target,
            IEnumerable<SiloAddress> silos) => silos;
    }

    private sealed class TestOptions;

    private sealed class ConstructorInjectedOptionFormatter : IOptionFormatter<TestOptions>
    {
        public ConstructorInjectedOptionFormatter(ActivationMarker dependency)
        {
            Dependency = dependency;
        }

        public ActivationMarker Dependency { get; }

        public string Name => nameof(TestOptions);

        public IEnumerable<string> Format() => Array.Empty<string>();
    }

    private sealed class ConstructorInjectedOptionFormatterResolver : IOptionFormatterResolver<TestOptions>
    {
        public ConstructorInjectedOptionFormatterResolver(ActivationMarker dependency)
        {
            Dependency = dependency;
        }

        public ActivationMarker Dependency { get; }

        public IOptionFormatter<TestOptions> Resolve(string name) => throw new NotSupportedException();
    }

    private sealed class TestClientBuilder : IClientBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    private sealed class ConstructorInjectedConnectionRetryFilter : IClientConnectionRetryFilter
    {
        public ConstructorInjectedConnectionRetryFilter(ActivationMarker dependency)
        {
            Dependency = dependency;
        }

        public ActivationMarker Dependency { get; }

        public Task<bool> ShouldRetryConnectionAttempt(Exception exception, CancellationToken cancellationToken)
            => Task.FromResult(false);
    }

    private sealed class ConstructorInjectedClusterConnectionStatusObserver : IClusterConnectionStatusObserver
    {
        public ConstructorInjectedClusterConnectionStatusObserver(ActivationMarker dependency)
        {
            Dependency = dependency;
        }

        public ActivationMarker Dependency { get; }

        public void NotifyGatewayCountChanged(
            int currentNumberOfGateways,
            int previousNumberOfGateways,
            bool connectionRecovered)
        {
        }

        public void NotifyClusterConnectionLost()
        {
        }
    }

    private sealed class ConstructorInjectedIncomingGrainCallFilter : IIncomingGrainCallFilter
    {
        public ConstructorInjectedIncomingGrainCallFilter(ActivationMarker dependency)
        {
            Dependency = dependency;
        }

        public ActivationMarker Dependency { get; }

        public Task Invoke(IIncomingGrainCallContext context) => Task.CompletedTask;
    }

    private sealed class ConstructorInjectedOutgoingGrainCallFilter : IOutgoingGrainCallFilter
    {
        public ConstructorInjectedOutgoingGrainCallFilter(ActivationMarker dependency)
        {
            Dependency = dependency;
        }

        public ActivationMarker Dependency { get; }

        public Task Invoke(IOutgoingGrainCallContext context) => Task.CompletedTask;
    }
}
