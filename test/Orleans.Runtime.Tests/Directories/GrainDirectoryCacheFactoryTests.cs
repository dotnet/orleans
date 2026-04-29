using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using TestExtensions;
using Xunit;

namespace Tester.Directories;

[TestCategory("BVT"), TestCategory("Directory")]
public class GrainDirectoryCacheFactoryTests
{
    [Fact]
    public async Task CreateGrainDirectoryCache_LruHonorsMaximumCacheTtl()
    {
        var timeProvider = new FakeTimeProvider();
        var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(timeProvider)
            .BuildServiceProvider();
        var options = new GrainDirectoryOptions
        {
            CacheSize = 10,
            MaximumCacheTTL = TimeSpan.FromMinutes(1)
        };
        var cache = GrainDirectoryCacheFactory.CreateGrainDirectoryCache(services, options);
        var disposableCache = Assert.IsAssignableFrom<IAsyncDisposable>(cache);
        using var listener = new ConcurrentLruCacheExpirationCleanupListener(cache);
        var address = CreateGrainAddress();

        try
        {
            cache.AddOrUpdate(address, version: 1);
            Assert.True(cache.LookUp(address.GrainId, out var result, out var version));
            Assert.Equal(address, result);
            Assert.Equal(1, version);

            timeProvider.Advance(TimeSpan.FromMinutes(2));
            var cleanup = await listener.WaitForCleanupAsync();

            Assert.Equal(1, cleanup);
            Assert.False(cache.LookUp(address.GrainId, out _, out _));
            Assert.Empty(cache.KeyValues);
        }
        finally
        {
            await disposableCache.DisposeAsync();
        }
    }

    [Fact]
    public void CreateGrainDirectoryCache_CustomDoesNotWrapRegisteredCache()
    {
        var expected = new TestGrainDirectoryCache();
        var services = new ServiceCollection()
            .AddSingleton<IGrainDirectoryCache>(expected)
            .BuildServiceProvider();
        var options = new GrainDirectoryOptions
        {
            CachingStrategy = GrainDirectoryOptions.CachingStrategyType.Custom
        };

        var actual = GrainDirectoryCacheFactory.CreateGrainDirectoryCache(services, options);

        Assert.Same(expected, actual);
    }

    private static GrainAddress CreateGrainAddress() => new()
    {
        ActivationId = ActivationId.NewId(),
        GrainId = GrainId.Parse($"user/{Guid.NewGuid():N}"),
        SiloAddress = SiloAddress.FromParsableString("127.0.0.1:11111@1"),
        MembershipVersion = new MembershipVersion(1)
    };

    private sealed class TestGrainDirectoryCache : IGrainDirectoryCache
    {
        public IEnumerable<(GrainAddress ActivationAddress, int Version)> KeyValues => [];

        public void AddOrUpdate(GrainAddress value, int version)
        {
        }

        public void Clear()
        {
        }

        public bool LookUp(GrainId key, out GrainAddress result, out int version)
        {
            result = default;
            version = default;
            return false;
        }

        public bool Remove(GrainId key) => false;

        public bool Remove(GrainAddress key) => false;
    }
}
