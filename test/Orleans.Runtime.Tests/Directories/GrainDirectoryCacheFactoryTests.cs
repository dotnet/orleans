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

    [Fact]
    public async Task CreateGrainDirectoryCache_LruReturnsOwnedCache()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var options = new GrainDirectoryOptions
        {
            CachingStrategy = GrainDirectoryOptions.CachingStrategyType.LRU,
            CacheSize = 10
        };

        var cache = GrainDirectoryCacheFactory.CreateGrainDirectoryCache(services, options, out var disposeCache);
        var disposableCache = Assert.IsAssignableFrom<IAsyncDisposable>(cache);

        try
        {
            Assert.True(disposeCache);
            Assert.IsAssignableFrom<IGrainDirectoryCache>(cache);
        }
        finally
        {
            await disposableCache.DisposeAsync();
        }
    }

    [Fact]
    public void CreateGrainDirectoryCache_NoneReturnsOwnedCache()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var options = new GrainDirectoryOptions
        {
            CachingStrategy = GrainDirectoryOptions.CachingStrategyType.None,
            CacheSize = 10
        };

        var cache = GrainDirectoryCacheFactory.CreateGrainDirectoryCache(services, options, out var disposeCache);

        Assert.True(disposeCache);
        Assert.IsAssignableFrom<IGrainDirectoryCache>(cache);
    }

    [Fact]
    public void CreateGrainDirectoryCache_NonPositiveCacheSizeReturnsOwnedCache()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var options = new GrainDirectoryOptions
        {
            CachingStrategy = GrainDirectoryOptions.CachingStrategyType.LRU,
            CacheSize = 0
        };

        var cache = GrainDirectoryCacheFactory.CreateGrainDirectoryCache(services, options, out var disposeCache);

        Assert.True(disposeCache);
        Assert.IsAssignableFrom<IGrainDirectoryCache>(cache);
    }

    [Fact]
    public void CreateGrainDirectoryCache_CustomReturnsUnownedRegisteredCache()
    {
        var expected = new TestGrainDirectoryCache();
        var services = new ServiceCollection()
            .AddSingleton<IGrainDirectoryCache>(expected)
            .BuildServiceProvider();
        var options = new GrainDirectoryOptions
        {
            CachingStrategy = GrainDirectoryOptions.CachingStrategyType.Custom
        };

        var actual = GrainDirectoryCacheFactory.CreateGrainDirectoryCache(services, options, out var disposeCache);

        Assert.False(disposeCache);
        Assert.Same(expected, actual);
    }

    [Fact]
    public void CreateCustomGrainDirectoryCache_ReturnsUnownedRegisteredCache()
    {
        var expected = new TestGrainDirectoryCache();
        var services = new ServiceCollection()
            .AddSingleton<IGrainDirectoryCache>(expected)
            .BuildServiceProvider();
        var options = new GrainDirectoryOptions();

        var actual = GrainDirectoryCacheFactory.CreateCustomGrainDirectoryCache(services, options, out var disposeCache);

        Assert.False(disposeCache);
        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task CreateCustomGrainDirectoryCache_FallbackReturnsOwnedLruCache()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var options = new GrainDirectoryOptions
        {
            CacheSize = 10
        };

        var cache = GrainDirectoryCacheFactory.CreateCustomGrainDirectoryCache(services, options, out var disposeCache);
        var disposableCache = Assert.IsAssignableFrom<IAsyncDisposable>(cache);

        try
        {
            Assert.True(disposeCache);
            Assert.IsAssignableFrom<IGrainDirectoryCache>(cache);
        }
        finally
        {
            await disposableCache.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeGrainDirectoryCacheAsync_AsyncDisposableCacheCallsDisposeAsync()
    {
        var cache = new AsyncDisposableGrainDirectoryCache();

        await GrainDirectoryCacheFactory.DisposeGrainDirectoryCacheAsync(cache);

        Assert.True(cache.DisposeAsyncCalled);
    }

    [Fact]
    public async Task DisposeGrainDirectoryCacheAsync_DisposableOnlyCacheCallsDispose()
    {
        var cache = new DisposableGrainDirectoryCache();

        await GrainDirectoryCacheFactory.DisposeGrainDirectoryCacheAsync(cache);

        Assert.True(cache.DisposeCalled);
    }

    [Fact]
    public async Task DisposeGrainDirectoryCacheAsync_NonDisposableCacheCompletes()
    {
        var cache = new TestGrainDirectoryCache();

        await GrainDirectoryCacheFactory.DisposeGrainDirectoryCacheAsync(cache);
    }

    private static GrainAddress CreateGrainAddress() => new()
    {
        ActivationId = ActivationId.NewId(),
        GrainId = GrainId.Parse($"user/{Guid.NewGuid():N}"),
        SiloAddress = SiloAddress.FromParsableString("127.0.0.1:11111@1"),
        MembershipVersion = new MembershipVersion(1)
    };

    private class TestGrainDirectoryCache : IGrainDirectoryCache
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

    private sealed class AsyncDisposableGrainDirectoryCache : TestGrainDirectoryCache, IAsyncDisposable
    {
        public bool DisposeAsyncCalled { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeAsyncCalled = true;
            return default;
        }
    }

    private sealed class DisposableGrainDirectoryCache : TestGrainDirectoryCache, IDisposable
    {
        public bool DisposeCalled { get; private set; }

        public void Dispose() => DisposeCalled = true;
    }
}
