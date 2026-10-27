using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
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
}
