using Microsoft.Extensions.Options;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.TypeSystem;
using Xunit;

namespace UnitTests.Manifest;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public sealed class GrainInterfaceTypeResolverTests(ITestOutputHelper output)
{
    [Fact]
    public void WarmConventionLookupDoesNotAllocate()
    {
        var resolver = CreateResolver();
        var type = typeof(ITestInterface<List<int>>);
        var expected = resolver.GetGrainInterfaceType(type);
        for (var i = 0; i < 100; i++)
        {
            _ = resolver.GetGrainInterfaceType(type);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            _ = resolver.GetGrainInterfaceType(type);
        }

        var cachedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            _ = resolver.GetGrainInterfaceTypeByConvention(type);
        }

        var conventionBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"1000 lookups: cached={cachedBytes} bytes; uncached convention={conventionBytes} bytes");
        Assert.Equal(0, cachedBytes);
        Assert.True(conventionBytes > 0);
        Assert.Equal(expected, resolver.GetGrainInterfaceTypeByConvention(type));
    }

    [Fact]
    public void ConfiguredIdentityIsCachedWithinItsResolver()
    {
        var firstProvider = new IdentityProvider("first`1");
        var secondProvider = new IdentityProvider("second`1");
        var first = CreateResolver(firstProvider);
        var second = CreateResolver(secondProvider);
        var type = typeof(ITestInterface<int>);
        var firstIdentity = first.GetGrainInterfaceType(type);
        var secondIdentity = second.GetGrainInterfaceType(type);
        Assert.NotEqual(firstIdentity, secondIdentity);
        for (var i = 0; i < 1000; i++)
        {
            Assert.Equal(firstIdentity, first.GetGrainInterfaceType(type));
            Assert.Equal(secondIdentity, second.GetGrainInterfaceType(type));
        }

        Assert.Equal(1, firstProvider.Calls);
        Assert.Equal(1, secondProvider.Calls);
        Assert.True(GenericGrainInterfaceType.TryParse(firstIdentity, out var generic));
        Assert.Equal([typeof(int)], generic.GetArguments(CreateConverter()));
        var stringIdentity = first.GetGrainInterfaceType(typeof(ITestInterface<string>));
        Assert.NotEqual(firstIdentity, stringIdentity);
        Assert.True(GenericGrainInterfaceType.TryParse(stringIdentity, out generic));
        Assert.Equal([typeof(string)], generic.GetArguments(CreateConverter()));
    }

    [Fact]
    public void ProviderPrecedenceDoesNotChangeConventionLookup()
    {
        var preferred = new IdentityProvider("preferred`1");
        var fallback = new IdentityProvider("fallback`1");
        var resolver = CreateResolver(preferred, fallback);
        var type = typeof(ITestInterface<>);
        Assert.Equal(GrainInterfaceType.Create("preferred`1"), resolver.GetGrainInterfaceType(type));
        Assert.NotEqual(resolver.GetGrainInterfaceType(type), resolver.GetGrainInterfaceTypeByConvention(type));
        Assert.Equal(1, preferred.Calls);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public void FailedResolutionCanBeRetriedAndInvalidTypesAreNotCached()
    {
        var provider = new IdentityProvider("retry") { Fail = true };
        var resolver = CreateResolver(provider);
        Assert.Throws<InvalidOperationException>(() => resolver.GetGrainInterfaceType(typeof(ITestInterface<int>)));
        provider.Fail = false;
        var expected = resolver.GetGrainInterfaceType(typeof(ITestInterface<int>));
        Assert.Equal(expected, resolver.GetGrainInterfaceType(typeof(ITestInterface<int>)));
        Assert.Equal(2, provider.Calls);
        Assert.Throws<ArgumentException>(() => resolver.GetGrainInterfaceType(typeof(int)));
        Assert.Throws<ArgumentException>(() => resolver.GetGrainInterfaceType(typeof(int)));
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task ConcurrentLookupsPublishTheSameIdentity()
    {
        var resolver = CreateResolver();
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(
            () => resolver.GetGrainInterfaceType(typeof(ITestInterface<List<int>>)), TestContext.Current.CancellationToken)));
        Assert.All(results, identity => Assert.Equal(results[0], identity));
        Assert.True(GenericGrainInterfaceType.TryParse(results[0], out var generic));
        Assert.Equal([typeof(List<int>)], generic.GetArguments(CreateConverter()));
    }

    private static GrainInterfaceTypeResolver CreateResolver(params IGrainInterfaceTypeProvider[] providers) => new(providers, CreateConverter());

    private static TypeConverter CreateConverter() => new(
        Array.Empty<ITypeConverter>(), Array.Empty<ITypeNameFilter>(), Array.Empty<ITypeFilter>(),
        Options.Create(new TypeManifestOptions { AllowAllTypes = true }), new CachedTypeResolver());

    private interface ITestInterface<T> { }

    private sealed class IdentityProvider(string identity) : IGrainInterfaceTypeProvider
    {
        public int Calls;
        public bool Fail;

        public bool TryGetGrainInterfaceType(Type type, out GrainInterfaceType result)
        {
            Interlocked.Increment(ref Calls);
            if (Fail)
            {
                throw new InvalidOperationException("Injected identity resolution failure");
            }

            result = GrainInterfaceType.Create(identity);
            return true;
        }
    }
}
