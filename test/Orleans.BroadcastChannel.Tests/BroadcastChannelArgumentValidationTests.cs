using System;
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.BroadcastChannel;
using Orleans.Hosting;
using Orleans.Metadata;
using Orleans.Runtime;
using Xunit;

namespace UnitTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
[TestCategory("BVT"), TestCategory("BroadcastChannel")]
public class BroadcastChannelArgumentValidationTests
{
    private const string ProviderName = "phase-one-provider";
    private const string OtherProviderName = "other-provider";

    [Fact]
    public void Create_NullKey_ThrowsWithExactParamName()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => ChannelId.Create("namespace", key: null!));

        Assert.Equal("key", exception.ParamName);
    }

    [Theory]
    [InlineData("namespace", "namespace/key")]
    [InlineData(null, "null/key")]
    public void Create_ValidStringKey_PreservesIdentityEqualityHashAndFormatting(string? channelNamespace, string expectedFormatting)
    {
        var channelId = ChannelId.Create(channelNamespace!, "key");
        var equivalent = ChannelId.Create(channelNamespace!, "key");

        Assert.Equal("key", channelId.GetKeyAsString());
        Assert.Equal(channelNamespace, channelId.GetNamespace());
        Assert.Equal(equivalent, channelId);
        Assert.True(channelId == equivalent);
        Assert.False(channelId != equivalent);
        Assert.Equal(equivalent.GetHashCode(), channelId.GetHashCode());
        Assert.Equal(expectedFormatting, channelId.ToString());
    }

    [Fact]
    public void GetObjectData_NullInfo_ThrowsWithExactParamName()
    {
        var channelId = ChannelId.Create("namespace", "key");

        var exception = Assert.Throws<ArgumentNullException>(() => channelId.GetObjectData(null!, default));

        Assert.Equal("info", exception.ParamName);
    }

    [Fact]
    public void DefaultPredicateProvider_NullPattern_ThrowsWithoutAssigningOutPredicate()
    {
        var provider = new DefaultChannelNamespacePredicateProvider();
        var sentinel = new SentinelPredicate();
        IChannelNamespacePredicate? predicate = sentinel;

        var exception = Assert.Throws<ArgumentNullException>(() => provider.TryGetPredicate(null!, out predicate));

        Assert.Equal("predicatePattern", exception.ParamName);
        Assert.Same(sentinel, predicate);
    }

    [Fact]
    public void ConstructorPredicateProvider_NullPattern_ThrowsWithoutAssigningOutPredicate()
    {
        var provider = new ConstructorChannelNamespacePredicateProvider();
        var sentinel = new SentinelPredicate();
        IChannelNamespacePredicate? predicate = sentinel;

        var exception = Assert.Throws<ArgumentNullException>(() => provider.TryGetPredicate(null!, out predicate));

        Assert.Equal("predicatePattern", exception.ParamName);
        Assert.Same(sentinel, predicate);
    }

    [Fact]
    public void GetGrainKeyId_NullGrainBindings_ThrowsBeforeReadingChannelId()
    {
        var mapper = new DefaultChannelIdMapper();

        var exception = Assert.Throws<ArgumentNullException>(() => mapper.GetGrainKeyId(null!, default));

        Assert.Equal("grainBindings", exception.ParamName);
    }

    [Fact]
    public void GetGrainKeyId_EmptyBindings_ReturnsChannelKey()
    {
        var mapper = new DefaultChannelIdMapper();
        var bindings = new GrainBindings(default, ImmutableArray<ImmutableDictionary<string, string>>.Empty);
        var channelId = ChannelId.Create("orders", "customer-42");

        var result = mapper.GetGrainKeyId(bindings, channelId);

        Assert.Equal(IdSpan.Create("customer-42"), result);
        Assert.Equal("customer-42", result.ToString());
    }

    [Fact]
    public void ImplicitSubscriptionAttribute_NullNamespace_ThrowsBeforeTrimOrAssignment()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new ImplicitChannelSubscriptionAttribute((string)null!));

        Assert.Equal("streamNamespace", exception.ParamName);
    }

    [Fact]
    public void ImplicitSubscriptionAttribute_ValidNamespace_TrimsAndCreatesExactMatchPredicate()
    {
        var attribute = new ImplicitChannelSubscriptionAttribute("  orders  ");

        Assert.Equal("namespace:orders", attribute.Predicate.PredicatePattern);
        Assert.True(attribute.Predicate.IsMatch("orders"));
        Assert.True(attribute.Predicate.IsMatch("  orders  "));
        Assert.False(attribute.Predicate.IsMatch("orders-priority"));
        Assert.Null(attribute.ChannelIdMapper);
    }

    [Theory]
    [InlineData(BuilderKind.Client, OptionsOverload.OptionsAction)]
    [InlineData(BuilderKind.Client, OptionsOverload.OptionsBuilderAction)]
    [InlineData(BuilderKind.Silo, OptionsOverload.OptionsAction)]
    [InlineData(BuilderKind.Silo, OptionsOverload.OptionsBuilderAction)]
    public void AddBroadcastChannel_NullBuilder_ThrowsWithExactParamName(
        BuilderKind builderKind,
        OptionsOverload optionsOverload)
    {
        var callbackCount = 0;
        Action<BroadcastChannelOptions> optionsAction = _ => callbackCount++;
        Action<OptionsBuilder<BroadcastChannelOptions>> optionsBuilderAction = _ => callbackCount++;

        var exception = Assert.Throws<ArgumentNullException>(
            () => AddBroadcastChannel(
                builder: null,
                builderKind,
                optionsOverload,
                ProviderName,
                optionsAction,
                optionsBuilderAction));

        Assert.Equal("this", exception.ParamName);
        Assert.Equal(0, callbackCount);
    }

    [Theory]
    [InlineData(BuilderKind.Client, OptionsOverload.OptionsAction)]
    [InlineData(BuilderKind.Client, OptionsOverload.OptionsBuilderAction)]
    [InlineData(BuilderKind.Silo, OptionsOverload.OptionsAction)]
    [InlineData(BuilderKind.Silo, OptionsOverload.OptionsBuilderAction)]
    public void AddBroadcastChannel_NullName_ThrowsBeforeCallbackOrServiceMutation(
        BuilderKind builderKind,
        OptionsOverload optionsOverload)
    {
        var builder = CreateBuilder(builderKind);
        var services = GetServices(builder, builderKind);
        var initialServiceCount = services.Count;
        var callbackCount = 0;
        Action<BroadcastChannelOptions> optionsAction = _ => callbackCount++;
        Action<OptionsBuilder<BroadcastChannelOptions>> optionsBuilderAction = _ => callbackCount++;

        var exception = Assert.Throws<ArgumentNullException>(
            () => AddBroadcastChannel(
                builder,
                builderKind,
                optionsOverload,
                name: null!,
                optionsAction,
                optionsBuilderAction));

        Assert.Equal("name", exception.ParamName);
        Assert.Equal(0, callbackCount);
        Assert.Equal(initialServiceCount, services.Count);
    }

    [Theory]
    [InlineData(BuilderKind.Client)]
    [InlineData(BuilderKind.Silo)]
    public void AddBroadcastChannel_NullOptionsCallback_ThrowsBeforeServiceMutation(BuilderKind builderKind)
    {
        var builder = CreateBuilder(builderKind);
        var services = GetServices(builder, builderKind);
        var initialServiceCount = services.Count;

        var exception = Assert.Throws<ArgumentNullException>(
            () => AddBroadcastChannel(
                builder,
                builderKind,
                OptionsOverload.OptionsAction,
                ProviderName,
                optionsAction: null,
                optionsBuilderAction: null));

        Assert.Equal("configureOptions", exception.ParamName);
        Assert.Equal(initialServiceCount, services.Count);
    }

    [Theory]
    [InlineData(BuilderKind.Client)]
    [InlineData(BuilderKind.Silo)]
    public void AddBroadcastChannel_NullOptionsBuilderCallback_RemainsOptional(BuilderKind builderKind)
    {
        var builder = CreateBuilder(builderKind);
        var services = GetServices(builder, builderKind);

        var result = AddBroadcastChannel(
            builder,
            builderKind,
            OptionsOverload.OptionsBuilderAction,
            ProviderName,
            optionsAction: null,
            optionsBuilderAction: null);

        Assert.Same(builder, result);
        AssertNamedProviderRegistration(services, ProviderName);
        AssertNoNamedProviderRegistration(services, OtherProviderName);
    }

    [Theory]
    [InlineData(BuilderKind.Client)]
    [InlineData(BuilderKind.Silo)]
    public void AddBroadcastChannel_OptionsAction_ConfiguresOnlyWhenNamedOptionsResolve(BuilderKind builderKind)
    {
        var builder = CreateBuilder(builderKind);
        var services = GetServices(builder, builderKind);
        var callbackCount = 0;

        var result = AddBroadcastChannel(
            builder,
            builderKind,
            OptionsOverload.OptionsAction,
            ProviderName,
            options =>
            {
                callbackCount++;
                options.FireAndForgetDelivery = false;
            },
            optionsBuilderAction: null);

        Assert.Same(builder, result);
        Assert.Equal(0, callbackCount);
        AssertNamedProviderRegistration(services, ProviderName);

        using var serviceProvider = services.BuildServiceProvider();
        var optionsMonitor = serviceProvider.GetRequiredService<IOptionsMonitor<BroadcastChannelOptions>>();
        var otherOptions = optionsMonitor.Get(OtherProviderName);
        Assert.True(otherOptions.FireAndForgetDelivery);
        Assert.Equal(0, callbackCount);

        var namedOptions = optionsMonitor.Get(ProviderName);
        Assert.False(namedOptions.FireAndForgetDelivery);
        Assert.Equal(1, callbackCount);
        Assert.Same(namedOptions, optionsMonitor.Get(ProviderName));
        Assert.Equal(1, callbackCount);
    }

    [Theory]
    [InlineData(BuilderKind.Client)]
    [InlineData(BuilderKind.Silo)]
    public void AddBroadcastChannel_OptionsBuilderAction_ConfiguresExpectedNamedRegistration(BuilderKind builderKind)
    {
        var builder = CreateBuilder(builderKind);
        var services = GetServices(builder, builderKind);
        var callbackCount = 0;
        string? callbackName = null;

        var result = AddBroadcastChannel(
            builder,
            builderKind,
            OptionsOverload.OptionsBuilderAction,
            ProviderName,
            optionsAction: null,
            optionsBuilder =>
            {
                callbackCount++;
                callbackName = optionsBuilder.Name;
                optionsBuilder.Configure(options => options.FireAndForgetDelivery = false);
            });

        Assert.Same(builder, result);
        Assert.Equal(1, callbackCount);
        Assert.Equal(ProviderName, callbackName);
        AssertNamedProviderRegistration(services, ProviderName);

        using var serviceProvider = services.BuildServiceProvider();
        var optionsMonitor = serviceProvider.GetRequiredService<IOptionsMonitor<BroadcastChannelOptions>>();
        Assert.True(optionsMonitor.Get(OtherProviderName).FireAndForgetDelivery);
        Assert.False(optionsMonitor.Get(ProviderName).FireAndForgetDelivery);
        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public void GetBroadcastChannelProvider_NullClient_ThrowsWithExactParamName()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => ChannelHostingExtensions.GetBroadcastChannelProvider(null!, ProviderName));

        Assert.Equal("this", exception.ParamName);
    }

    [Fact]
    public void GetBroadcastChannelProvider_NullName_ThrowsWithExactParamName()
    {
        var client = DispatchProxy.Create<IClusterClient, ThrowingClusterClientProxy>();

        var exception = Assert.Throws<ArgumentNullException>(
            () => ChannelHostingExtensions.GetBroadcastChannelProvider(client, name: null!));

        Assert.Equal("name", exception.ParamName);
    }

    private static object CreateBuilder(BuilderKind builderKind)
        => builderKind switch
        {
            BuilderKind.Client => new TestClientBuilder(),
            BuilderKind.Silo => new TestSiloBuilder(),
            _ => throw new ArgumentOutOfRangeException(nameof(builderKind)),
        };

    private static IServiceCollection GetServices(object builder, BuilderKind builderKind)
        => builderKind switch
        {
            BuilderKind.Client => ((IClientBuilder)builder).Services,
            BuilderKind.Silo => ((ISiloBuilder)builder).Services,
            _ => throw new ArgumentOutOfRangeException(nameof(builderKind)),
        };

    private static object AddBroadcastChannel(
        object? builder,
        BuilderKind builderKind,
        OptionsOverload optionsOverload,
        string name,
        Action<BroadcastChannelOptions>? optionsAction,
        Action<OptionsBuilder<BroadcastChannelOptions>>? optionsBuilderAction)
        => (builderKind, optionsOverload) switch
        {
            (BuilderKind.Client, OptionsOverload.OptionsAction) => ChannelHostingExtensions.AddBroadcastChannel(
                (IClientBuilder)builder!,
                name,
                optionsAction!),
            (BuilderKind.Client, OptionsOverload.OptionsBuilderAction) => ChannelHostingExtensions.AddBroadcastChannel(
                (IClientBuilder)builder!,
                name,
                optionsBuilderAction),
            (BuilderKind.Silo, OptionsOverload.OptionsAction) => ChannelHostingExtensions.AddBroadcastChannel(
                (ISiloBuilder)builder!,
                name,
                optionsAction!),
            (BuilderKind.Silo, OptionsOverload.OptionsBuilderAction) => ChannelHostingExtensions.AddBroadcastChannel(
                (ISiloBuilder)builder!,
                name,
                optionsBuilderAction),
            _ => throw new ArgumentOutOfRangeException(nameof(optionsOverload)),
        };

    private static void AssertNamedProviderRegistration(IServiceCollection services, string name)
    {
        var registration = Assert.Single(
            services,
            descriptor => descriptor.IsKeyedService
                && descriptor.ServiceType == typeof(IBroadcastChannelProvider)
                && Equals(descriptor.ServiceKey, name));

        Assert.Equal(name, registration.ServiceKey);
        Assert.NotNull(registration.KeyedImplementationFactory);
    }

    private static void AssertNoNamedProviderRegistration(IServiceCollection services, string name)
        => Assert.DoesNotContain(
            services,
            descriptor => descriptor.IsKeyedService
                && descriptor.ServiceType == typeof(IBroadcastChannelProvider)
                && Equals(descriptor.ServiceKey, name));

    private sealed class SentinelPredicate : IChannelNamespacePredicate
    {
        public string PredicatePattern => "sentinel";

        public bool IsMatch(string streamNamespace) => false;
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    private sealed class TestClientBuilder : IClientBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    private class ThrowingClusterClientProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new InvalidOperationException($"Unexpected client invocation: {targetMethod?.Name}");
    }

    public enum BuilderKind
    {
        Client,
        Silo,
    }

    public enum OptionsOverload
    {
        OptionsAction,
        OptionsBuilderAction,
    }
}
