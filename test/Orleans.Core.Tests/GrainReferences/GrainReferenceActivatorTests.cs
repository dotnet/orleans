using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Serializers;
using TestExtensions;
using Xunit;

namespace UnitTests.GrainReferences;

[TestCategory("BVT")]
public class GrainReferenceActivatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateReference_GeneratedProxyPreservesIdentityAndSharedState(bool unordered)
    {
        using var fixture = new ReferenceConstructionFixture(unordered);
        var first = fixture.CreateReference("first-key");
        var second = fixture.CreateReference("second-key");
        var repeated = fixture.CreateReference("first-key");

        Assert.IsAssignableFrom<IConstructionGrain>(first);
        Assert.IsAssignableFrom<IConstructionBaseGrain>(first);
        Assert.Equal(first.GetType(), second.GetType());
        Assert.Equal(GrainId.Create(ReferenceConstructionFixture.GrainType, IdSpan.Create("first-key")), first.GrainId);
        Assert.Equal(GrainId.Create(ReferenceConstructionFixture.GrainType, IdSpan.Create("second-key")), second.GrainId);
        Assert.Equal(ReferenceConstructionFixture.InterfaceType, first.InterfaceType);
        Assert.Equal(17, first.InterfaceVersion);
        Assert.NotSame(first, repeated);
        Assert.Equal(first, repeated);
        Assert.Same(first.Shared, second.Shared);
        Assert.Same(first.Shared, repeated.Shared);
        Assert.Same(fixture.Runtime, first.Shared.Runtime);
        Assert.Same(fixture.Services, first.Shared.ServiceProvider);
        Assert.Same(fixture.Services.GetRequiredService<CodecProvider>(), first.Shared.CodecProvider);
        Assert.Same(fixture.Services.GetRequiredService<CopyContextPool>(), first.Shared.CopyContextPool);
        Assert.Equal(unordered ? InvokeMethodOptions.Unordered : InvokeMethodOptions.None, first.Shared.InvokeMethodOptions);
        Assert.Same(first, first.Cast<IConstructionGrain>());
        Assert.Same(first, fixture.Runtime.CastReference);
        Assert.Equal(typeof(IConstructionGrain), fixture.Runtime.CastInterface);
    }

    [Fact]
    public void TryGet_UnknownInterfaceDeclinesActivation()
    {
        using var fixture = new ReferenceConstructionFixture();

        Assert.False(fixture.Provider.TryGet(
            ReferenceConstructionFixture.GrainType, GrainInterfaceType.Create("unknown"), out var activator));
        Assert.Null(activator);
        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Activator.CreateReference(
            GrainId.Create(ReferenceConstructionFixture.GrainType, IdSpan.Create("key")), GrainInterfaceType.Create("unknown")));
        Assert.Equal(
            $"Unable to find an IGrainReferenceActivatorProvider for grain type {ReferenceConstructionFixture.GrainType}",
            exception.Message);
    }

    [Fact]
    public void CreateReference_MissingProxyConstructorThrowsSerializerException()
    {
        using var fixture = new ReferenceConstructionFixture(unordered: false, proxyType: typeof(MissingConstructorProxy));

        var exception = Assert.Throws<SerializerException>(() => fixture.CreateReference("key"));

        Assert.Equal("Invalid proxy type: " + typeof(MissingConstructorProxy), exception.Message);
    }

    [Fact]
    public void CreateReference_ConstructorExceptionPropagatesDirectly()
    {
        using var fixture = new ReferenceConstructionFixture(unordered: false, proxyType: typeof(ThrowingConstructorProxy));

        var exception = Assert.Throws<ConstructionException>(() => fixture.CreateReference("key"));

        Assert.Equal("proxy-constructor", exception.Message);
    }

    [Fact]
    public void CreateReference_NonPublicProxyConstructorPreservesGrainId()
    {
        using var fixture = new ReferenceConstructionFixture(unordered: false, proxyType: typeof(NonPublicConstructorProxy));

        var reference = Assert.IsType<NonPublicConstructorProxy>(fixture.CreateReference("private-constructor"));

        Assert.Equal(
            GrainId.Create(ReferenceConstructionFixture.GrainType, IdSpan.Create("private-constructor")),
            reference.GrainId);
    }

    [Fact]
    public void CreateReference_StaticallyClosedGenericFactoriesPreserveIdentityAndSharedState()
    {
        using var fixture = new ReferenceConstructionFixture();
        var first = fixture.CreateReference<int>("int-key");
        var second = fixture.CreateReference<int>("second-key");
        var text = fixture.CreateReference<string>("text-key");

        Assert.IsAssignableFrom<IGenericConstructionGrain<int>>(first);
        Assert.IsAssignableFrom<IGenericConstructionGrain<string>>(text);
        Assert.NotEqual(first.GetType(), text.GetType());
        Assert.Equal(fixture.Resolver.GetGrainInterfaceType(typeof(IGenericConstructionGrain<int>)), first.InterfaceType);
        Assert.Equal(fixture.Resolver.GetGrainInterfaceType(typeof(IGenericConstructionGrain<string>)), text.InterfaceType);
        Assert.Equal(IdSpan.Create("int-key"), first.GrainId.Key);
        Assert.Equal(IdSpan.Create("text-key"), text.GrainId.Key);
        Assert.Same(first.Shared, second.Shared);
        Assert.NotSame(first.Shared, text.Shared);
        var factories = fixture.ManifestOptions.GetOrCreate<GrainReferenceFactoryOptions>().Factories;
        Assert.NotNull(factories[typeof(IGenericConstructionGrain<int>)].Factory);
        Assert.NotNull(factories[typeof(IGenericConstructionGrain<string>)].Factory);
    }

    [Fact]
    public void CreateReference_RuntimeSelectedGenericPreservesJitCompatibility()
    {
        using var fixture = new ReferenceConstructionFixture();

        var reference = fixture.CreateReference<Guid>("generic-key");

        Assert.IsAssignableFrom<IGenericConstructionGrain<Guid>>(reference);
        Assert.Equal(IdSpan.Create("generic-key"), reference.GrainId.Key);
        Assert.Equal(fixture.Resolver.GetGrainInterfaceType(typeof(IGenericConstructionGrain<Guid>)), reference.InterfaceType);
    }

    [Fact]
    public void CreateReference_LegacyGeneratedManifestPreservesCompatibility()
    {
        using var fixture = new ReferenceConstructionFixture(legacy: true);

        var reference = fixture.CreateReference("legacy-key");

        Assert.IsAssignableFrom<IConstructionGrain>(reference);
        Assert.Equal(IdSpan.Create("legacy-key"), reference.GrainId.Key);
        Assert.Equal(17, reference.InterfaceVersion);
    }

    [Fact]
    public void CreateReference_RegisteredFactoryUsesDeclaredInterfaceAndPropagatesConstructorExceptions()
    {
        using var fixture = new ReferenceConstructionFixture(
            proxyType: typeof(ThrowingConstructorProxy), factory: static (shared, key) => new ThrowingConstructorProxy(shared, key));

        var exception = Assert.Throws<ConstructionException>(() => fixture.CreateReference("key"));

        Assert.Equal("proxy-constructor", exception.Message);
    }

    [Fact]
    public void CreateReference_RegisteredFactorySupportsPrivateConstructor()
    {
        using var fixture = new ReferenceConstructionFixture(
            proxyType: typeof(NonPublicConstructorProxy), factory: NonPublicConstructorProxy.Create);

        var reference = Assert.IsType<NonPublicConstructorProxy>(fixture.CreateReference("private-key"));

        Assert.Equal(IdSpan.Create("private-key"), reference.GrainId.Key);
    }

    [Fact]
    public void CreateReference_LegacyCustomProxyOverridesGeneratedFactory()
    {
        using var fixture = new ReferenceConstructionFixture(
            proxyType: typeof(InspectableConstructionProxy), includeGeneratedFactories: true);
        var generated = fixture.ManifestOptions.GetOrCreate<GrainReferenceFactoryOptions>().Factories[typeof(IConstructionGrain)];
        Assert.NotNull(generated.Factory);
        Assert.NotEqual(typeof(InspectableConstructionProxy), generated.ProxyType);

        var reference = Assert.IsType<InspectableConstructionProxy>(fixture.CreateReference("override-key"));

        Assert.Equal(IdSpan.Create("override-key"), reference.GrainId.Key);
        Assert.Equal(17, reference.InterfaceVersion);
    }

    [Fact]
    public void CreateReference_RegisteredCustomFactoryOverridesGeneratedFactory()
    {
        using var fixture = new ReferenceConstructionFixture(
            proxyType: typeof(ThrowingConstructorProxy),
            factory: static (shared, key) => new ThrowingConstructorProxy(shared, key),
            includeGeneratedFactories: true);

        var exception = Assert.Throws<ConstructionException>(() => fixture.CreateReference("override-key"));

        Assert.Equal("proxy-constructor", exception.Message);
    }

    [Fact]
    public void CreateReference_RegisteredFactoryUsesExplicitInterfaceForMultipleInterfaceProxy()
    {
        using var fixture = new ReferenceConstructionFixture(
            proxyType: typeof(MultipleInterfaceConstructionProxy),
            factory: static (shared, key) => new MultipleInterfaceConstructionProxy(shared, key));

        var reference = Assert.IsType<MultipleInterfaceConstructionProxy>(fixture.CreateReference("multiple-key"));

        Assert.IsAssignableFrom<IOtherConstructionGrain>(reference);
        Assert.Equal(ReferenceConstructionFixture.InterfaceType, reference.InterfaceType);
        Assert.Equal(17, reference.InterfaceVersion);
        Assert.Equal(IdSpan.Create("multiple-key"), reference.GrainId.Key);
    }

    [Fact]
    public void ManifestExtensionsPreserveTypedConfigurationPerManifest()
    {
        var first = new TypeManifestOptions();
        var second = new TypeManifestOptions();
        var factories = first.GetOrCreate<GrainReferenceFactoryOptions>();
        factories.Add(typeof(IConstructionGrain), typeof(InspectableConstructionProxy),
            static (shared, key) => new InspectableConstructionProxy(shared, key));

        Assert.Same(factories, first.GetOrCreate<GrainReferenceFactoryOptions>());
        Assert.NotSame(factories, second.GetOrCreate<GrainReferenceFactoryOptions>());
        Assert.Empty(second.GetOrCreate<GrainReferenceFactoryOptions>().Factories);
        Assert.Single(first.GetOrCreate<GrainReferenceFactoryOptions>().Factories);
    }
}
