using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.TypeSystem;

namespace Orleans.CodeGenerator.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("CodeGen")]
public sealed class SerializerInitializationTests
{
    [Fact]
    public void AddingAutomaticServicesAfterContextInitializesThemOnce()
    {
        var services = new ServiceCollection()
            .AddSerializerContext(new ManualContext())
            .AddSerializer()
            .AddSerializer();
        Assert.Equal(3, services.Count(descriptor => descriptor.ServiceType == typeof(IGeneralizedCodec)));
        using var provider = services.BuildServiceProvider();
        Assert.IsType<CachedTypeResolver>(provider.GetRequiredService<TypeResolver>());
        Assert.IsType<StringCodec>(provider.GetRequiredService<CodecProvider>().GetCodec<string>());
    }

    [Fact]
    public void ContextServicesUseTheCommonTypeResolver()
    {
        using var provider = new ServiceCollection().AddSerializerContext(new ManualContext()).BuildServiceProvider();
        var resolver = provider.GetRequiredService<TypeResolver>();
        Assert.IsType<CachedTypeResolver>(resolver);
        Assert.Equal(typeof(int), resolver.ResolveType("System.Int32"));
        Assert.Equal(typeof(string), resolver.ResolveType("System.String"));
        Assert.False(resolver.TryResolveType("Missing.Serialization.Type", out _));
    }

    private sealed class ManualContext : SerializerContext
    {
        protected override void ConfigureInner(TypeManifestOptions options)
            => options.AddSerializer<int>(static _ => new Int32Codec(), static _ => new ShallowCopier<int>());
    }
}
