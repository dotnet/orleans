using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using TestExtensions;
using Xunit;

namespace Tester.Directories;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT"), TestCategory("Directory")]
public class GrainDirectoryCacheFactoryTests
{
    [Fact]
    public async Task CreateGrainDirectoryCache_LruHonorsMaximumCacheTtl()
    {
        var timeProvider = new FakeTimeProvider();
        var services = new ServiceCollection()
            .AddKeyedSingleton<TimeProvider>(TimeProviderNames.GrainDirectory, timeProvider)
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
            result = default!;
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

    [Fact]
    public async Task CreateGrainDirectoryCache_AddOrUpdateUpdatesSharedRouteHandle()
    {
        var (cache, entrySource) = CreateEntryCache();
        var disposableCache = Assert.IsAssignableFrom<IAsyncDisposable>(cache);
        var grainId = CreateGrainId();
        var originalAddress = CreateGrainAddress(grainId, port: 11111);
        var replacementAddress = CreateGrainAddress(grainId, port: 22222);
        var originalTarget = CreateMessageTarget();
        var replacementTarget = CreateMessageTarget();

        try
        {
            cache.AddOrUpdate(originalAddress, version: 1);
            var originalEntry = GetEntry(entrySource, grainId);
            Assert.True(originalEntry.TrySetMessageTarget(originalTarget, originalEntry.Address));

            cache.AddOrUpdate(replacementAddress, version: 2);
            var replacementEntry = GetEntry(entrySource, grainId);

            Assert.Same(originalEntry, replacementEntry);
            Assert.True(replacementEntry.IsValid);
            Assert.Equal(replacementAddress, replacementEntry.Address);
            Assert.Equal(2, replacementEntry.Version);
            Assert.False(replacementEntry.TryGetMessageTarget(out _));
            Assert.True(replacementEntry.TrySetMessageTarget(replacementTarget, replacementEntry.Address));
            AssertMessageTarget(replacementEntry, replacementTarget);
            Assert.True(cache.LookUp(grainId, out var result, out var version));
            Assert.Equal(replacementAddress, result);
            Assert.Equal(2, version);
        }
        finally
        {
            await disposableCache.DisposeAsync();
        }
    }

    [Fact]
    public async Task CreateGrainDirectoryCache_RemoveByGrainIdInvalidatesRouteHandle()
    {
        var (cache, entrySource) = CreateEntryCache();
        var disposableCache = Assert.IsAssignableFrom<IAsyncDisposable>(cache);
        var address = CreateGrainAddress(CreateGrainId(), port: 11111);
        var target = CreateMessageTarget();

        try
        {
            cache.AddOrUpdate(address, version: 3);
            var entry = GetEntry(entrySource, address.GrainId);
            Assert.True(entry.TrySetMessageTarget(target, entry.Address));

            Assert.True(cache.Remove(address.GrainId));

            AssertInvalidEntry(entry);
            Assert.False(cache.LookUp(address.GrainId, out _, out _));
            Assert.False(cache.Remove(address.GrainId));
            AssertInvalidEntry(entry);
        }
        finally
        {
            await disposableCache.DisposeAsync();
        }
    }

    [Fact]
    public async Task CreateGrainDirectoryCache_RemoveByAddressInvalidatesOnlyMatchingRouteHandle()
    {
        var (cache, entrySource) = CreateEntryCache();
        var disposableCache = Assert.IsAssignableFrom<IAsyncDisposable>(cache);
        var grainId = CreateGrainId();
        var address = CreateGrainAddress(grainId, port: 11111);
        var mismatchedAddress = CreateGrainAddress(grainId, port: 22222);
        var controlAddress = CreateGrainAddress(CreateGrainId(), port: 33333);
        var target = CreateMessageTarget();
        var controlTarget = CreateMessageTarget();

        try
        {
            cache.AddOrUpdate(address, version: 4);
            cache.AddOrUpdate(controlAddress, version: 5);
            var entry = GetEntry(entrySource, grainId);
            var controlEntry = GetEntry(entrySource, controlAddress.GrainId);
            Assert.True(entry.TrySetMessageTarget(target, entry.Address));
            Assert.True(controlEntry.TrySetMessageTarget(controlTarget, controlEntry.Address));

            Assert.False(cache.Remove(mismatchedAddress));
            AssertMessageTarget(entry, target);
            Assert.True(cache.LookUp(grainId, out var retainedAddress, out var retainedVersion));
            Assert.Equal(address, retainedAddress);
            Assert.Equal(4, retainedVersion);

            Assert.True(cache.Remove(address));

            AssertInvalidEntry(entry);
            Assert.False(cache.LookUp(grainId, out _, out _));
            AssertMessageTarget(controlEntry, controlTarget);
            Assert.True(cache.LookUp(controlAddress.GrainId, out var controlResult, out var controlVersion));
            Assert.Equal(controlAddress, controlResult);
            Assert.Equal(5, controlVersion);
        }
        finally
        {
            await disposableCache.DisposeAsync();
        }
    }

    [Fact]
    public async Task CreateGrainDirectoryCache_ClearInvalidatesAllRouteHandles()
    {
        var (cache, entrySource) = CreateEntryCache();
        var disposableCache = Assert.IsAssignableFrom<IAsyncDisposable>(cache);
        var addresses = new[]
        {
            CreateGrainAddress(CreateGrainId(), port: 11111),
            CreateGrainAddress(CreateGrainId(), port: 22222),
            CreateGrainAddress(CreateGrainId(), port: 33333)
        };
        var entries = new GrainDirectoryCacheEntry[addresses.Length];

        try
        {
            for (var i = 0; i < addresses.Length; i++)
            {
                cache.AddOrUpdate(addresses[i], version: i + 1);
                entries[i] = GetEntry(entrySource, addresses[i].GrainId);
                Assert.True(entries[i].TrySetMessageTarget(CreateMessageTarget(), entries[i].Address));
            }

            cache.Clear();

            Assert.Empty(cache.KeyValues);
            for (var i = 0; i < addresses.Length; i++)
            {
                AssertInvalidEntry(entries[i]);
                Assert.False(cache.LookUp(addresses[i].GrainId, out _, out _));
            }

            cache.Clear();
            Assert.Empty(cache.KeyValues);
            foreach (var entry in entries)
            {
                AssertInvalidEntry(entry);
            }
        }
        finally
        {
            await disposableCache.DisposeAsync();
        }
    }

    [Fact]
    public async Task CreateGrainDirectoryCache_ExpirationInvalidatesRouteHandle()
    {
        var timeProvider = new FakeTimeProvider();
        var timeToLive = TimeSpan.FromMinutes(1);
        var (cache, entrySource) = CreateEntryCache(cacheSize: 10, timeToLive, timeProvider);
        var disposableCache = Assert.IsAssignableFrom<IAsyncDisposable>(cache);
        using var listener = new ConcurrentLruCacheExpirationCleanupListener(cache);
        var expiredAddress = CreateGrainAddress(CreateGrainId(), port: 11111);
        var freshAddress = CreateGrainAddress(CreateGrainId(), port: 22222);

        try
        {
            cache.AddOrUpdate(expiredAddress, version: 6);
            var expiredEntry = GetEntry(entrySource, expiredAddress.GrainId);
            Assert.True(expiredEntry.TrySetMessageTarget(CreateMessageTarget(), expiredEntry.Address));

            timeProvider.Advance(timeToLive);
            Assert.Equal(0, await listener.WaitForCleanupAsync());

            cache.AddOrUpdate(freshAddress, version: 7);
            var freshEntry = GetEntry(entrySource, freshAddress.GrainId);
            var freshTarget = CreateMessageTarget();
            Assert.True(freshEntry.TrySetMessageTarget(freshTarget, freshEntry.Address));

            timeProvider.Advance(timeToLive);
            Assert.Equal(1, await listener.WaitForCleanupAsync());

            AssertInvalidEntry(expiredEntry);
            Assert.False(cache.LookUp(expiredAddress.GrainId, out _, out _));
            AssertMessageTarget(freshEntry, freshTarget);
            Assert.True(cache.LookUp(freshAddress.GrainId, out var result, out var version));
            Assert.Equal(freshAddress, result);
            Assert.Equal(7, version);
        }
        finally
        {
            await disposableCache.DisposeAsync();
        }
    }

    [Fact]
    public async Task CreateGrainDirectoryCache_RetainedHandleTouchRefreshesExpiration()
    {
        var timeProvider = new FakeTimeProvider();
        var timeToLive = TimeSpan.FromMinutes(1);
        var (cache, entrySource) = CreateEntryCache(cacheSize: 10, timeToLive, timeProvider);
        var disposableCache = Assert.IsAssignableFrom<IAsyncDisposable>(cache);
        using var listener = new ConcurrentLruCacheExpirationCleanupListener(cache);
        var address = CreateGrainAddress(CreateGrainId(), port: 11111);

        try
        {
            cache.AddOrUpdate(address, version: 1);
            var entry = GetEntry(entrySource, address.GrainId);

            timeProvider.Advance(timeToLive);
            Assert.Equal(0, await listener.WaitForCleanupAsync());
            Assert.True(entry.TryTouch());

            timeProvider.Advance(timeToLive);
            Assert.Equal(0, await listener.WaitForCleanupAsync());
            Assert.True(entry.IsValid);
            Assert.Single(cache.KeyValues);

            timeProvider.Advance(timeToLive);
            Assert.Equal(1, await listener.WaitForCleanupAsync());
            AssertInvalidEntry(entry);
        }
        finally
        {
            await disposableCache.DisposeAsync();
        }
    }

    [Fact]
    public async Task CreateGrainDirectoryCache_EvictionInvalidatesRouteHandle()
    {
        var (cache, entrySource) = CreateEntryCache(cacheSize: 3);
        var disposableCache = Assert.IsAssignableFrom<IAsyncDisposable>(cache);
        var addresses = new[]
        {
            CreateGrainAddress(CreateGrainId(), port: 11111),
            CreateGrainAddress(CreateGrainId(), port: 22222),
            CreateGrainAddress(CreateGrainId(), port: 33333),
            CreateGrainAddress(CreateGrainId(), port: 44444)
        };
        var entries = new GrainDirectoryCacheEntry[addresses.Length];
        var targets = new IGrainContext[addresses.Length];

        try
        {
            for (var i = 0; i < 3; i++)
            {
                cache.AddOrUpdate(addresses[i], version: i + 1);
                entries[i] = GetEntry(entrySource, addresses[i].GrainId);
                targets[i] = CreateMessageTarget();
                Assert.True(entries[i].TrySetMessageTarget(targets[i], entries[i].Address));
            }

            cache.AddOrUpdate(addresses[3], version: 4);
            entries[3] = GetEntry(entrySource, addresses[3].GrainId);
            targets[3] = CreateMessageTarget();
            Assert.True(entries[3].TrySetMessageTarget(targets[3], entries[3].Address));

            AssertInvalidEntry(entries[0]);
            Assert.False(entrySource.TryGetEntry(addresses[0].GrainId, out _));
            for (var i = 1; i < entries.Length; i++)
            {
                Assert.Same(entries[i], GetEntry(entrySource, addresses[i].GrainId));
                AssertMessageTarget(entries[i], targets[i]);
                Assert.True(cache.LookUp(addresses[i].GrainId, out var result, out var version));
                Assert.Equal(addresses[i], result);
                Assert.Equal(i + 1, version);
            }

            Assert.Equal(3, cache.KeyValues.Count());
        }
        finally
        {
            await disposableCache.DisposeAsync();
        }
    }

    [Fact]
    public void GrainDirectoryCacheEntry_DisposedEntryCannotBindMessageTarget()
    {
        var address = CreateGrainAddress(CreateGrainId(), port: 11111);
        var entry = new GrainDirectoryCacheEntry(address, version: 8);

        entry.Dispose();

        AssertInvalidEntry(entry);
        Assert.Equal(address, entry.Address);
        Assert.Equal(8, entry.Version);
    }

    [Fact]
    public void GrainDirectoryCacheEntry_SecondTargetCannotReplaceBoundTarget()
    {
        var entry = new GrainDirectoryCacheEntry(CreateGrainAddress(CreateGrainId(), port: 11111), version: 9);
        var originalTarget = CreateMessageTarget();

        Assert.True(entry.TrySetMessageTarget(originalTarget, entry.Address));
        Assert.False(entry.TrySetMessageTarget(CreateMessageTarget(), entry.Address));
        AssertMessageTarget(entry, originalTarget);
    }

    [Fact]
    public void GrainDirectoryCacheEntry_ClearRequiresBoundTargetIdentity()
    {
        var entry = new GrainDirectoryCacheEntry(CreateGrainAddress(CreateGrainId(), port: 11111), version: 10);
        var originalTarget = CreateMessageTarget();
        var replacementTarget = CreateMessageTarget();
        Assert.True(entry.TrySetMessageTarget(originalTarget, entry.Address));

        entry.ClearMessageTarget(CreateMessageTarget());
        AssertMessageTarget(entry, originalTarget);

        entry.ClearMessageTarget(originalTarget);
        Assert.False(entry.TryGetMessageTarget(out _));
        Assert.True(entry.TrySetMessageTarget(replacementTarget, entry.Address));
        AssertMessageTarget(entry, replacementTarget);
    }

    [Fact]
    public async Task GrainDirectoryCacheEntry_StaleAddressCannotRebindAfterUpdate()
    {
        var (cache, entrySource) = CreateEntryCache();
        var disposableCache = Assert.IsAssignableFrom<IAsyncDisposable>(cache);
        var grainId = CreateGrainId();
        var originalAddress = CreateGrainAddress(grainId, port: 11111);
        var replacementAddress = CreateGrainAddress(grainId, port: 22222);

        try
        {
            cache.AddOrUpdate(originalAddress, version: 1);
            var entry = GetEntry(entrySource, grainId);
            cache.AddOrUpdate(replacementAddress, version: 2);

            Assert.False(entry.TrySetMessageTarget(CreateMessageTarget(), originalAddress));
            Assert.False(entry.TryGetMessageTarget(out _));
            Assert.Equal(replacementAddress, entry.Address);
        }
        finally
        {
            await disposableCache.DisposeAsync();
        }
    }

    [Fact]
    public void GrainDirectoryCacheEntry_UpdateBlocksBindingUntilNewAddressIsPublished()
    {
        var grainId = CreateGrainId();
        var originalAddress = CreateGrainAddress(grainId, port: 11111);
        var replacementAddress = CreateGrainAddress(grainId, port: 22222);
        var entry = new GrainDirectoryCacheEntry(originalAddress, version: 1);
        var originalTarget = CreateMessageTarget();

        Assert.True(entry.TrySetMessageTarget(originalTarget, originalAddress));

        Assert.True(entry.TryBeginUpdate());
        Assert.False(entry.IsValid);
        Assert.False(entry.TryGetMessageTarget(out _));
        Assert.False(entry.TrySetMessageTarget(CreateMessageTarget(), originalAddress));

        entry.Value = (replacementAddress, 2);

        Assert.False(entry.IsValid);
        Assert.False(entry.TrySetMessageTarget(CreateMessageTarget(), replacementAddress));

        entry.EndUpdate();

        Assert.True(entry.IsValid);
        Assert.Equal(replacementAddress, entry.Address);
        Assert.Equal(2, entry.Version);
        Assert.False(entry.TryGetMessageTarget(out _));
        Assert.False(entry.TrySetMessageTarget(CreateMessageTarget(), originalAddress));

        var replacementTarget = CreateMessageTarget();
        Assert.True(entry.TrySetMessageTarget(replacementTarget, replacementAddress));
        AssertMessageTarget(entry, replacementTarget);
    }

    [Fact]
    public void GrainDirectoryCacheEntry_InvalidationDuringUpdateCannotReopenBinding()
    {
        var grainId = CreateGrainId();
        var originalAddress = CreateGrainAddress(grainId, port: 11111);
        var replacementAddress = CreateGrainAddress(grainId, port: 22222);
        var entry = new GrainDirectoryCacheEntry(originalAddress, version: 1);

        Assert.True(entry.TryBeginUpdate());

        entry.Invalidate();
        entry.Value = (replacementAddress, 2);
        entry.EndUpdate();

        AssertInvalidEntry(entry);
        Assert.Equal(replacementAddress, entry.Address);
        Assert.Equal(2, entry.Version);
    }

    [Fact]
    public async Task CreateGrainDirectoryCache_RemoveByGrainIdReleasesMessageTargetReference()
    {
        var (cache, entrySource) = CreateEntryCache();
        var disposableCache = Assert.IsAssignableFrom<IAsyncDisposable>(cache);
        var address = CreateGrainAddress(CreateGrainId(), port: 11111);

        try
        {
            cache.AddOrUpdate(address, version: 9);
            var entry = GetEntry(entrySource, address.GrainId);
            var targetReference = BindMessageTarget(entry);

            Assert.True(cache.Remove(address.GrainId));
            AssertInvalidEntry(entry);
            AssertEventuallyCollected(targetReference);
        }
        finally
        {
            await disposableCache.DisposeAsync();
        }
    }

    [Fact]
    public void CreateGrainDirectoryCache_LongLivedHandleDoesNotRetainRemovedEntry()
    {
        var handle = CreateRemovedEntryHandle();

        AssertEventuallyCollected(handle);
        GC.KeepAlive(handle);
    }

    [Fact]
    public async Task CreateGrainDirectoryCache_DisposeInvalidatesHandleAndReleasesTarget()
    {
        var (cache, entrySource) = CreateEntryCache();
        var disposableCache = Assert.IsAssignableFrom<IAsyncDisposable>(cache);
        var address = CreateGrainAddress(CreateGrainId(), port: 11111);
        cache.AddOrUpdate(address, version: 1);
        var entry = GetEntry(entrySource, address.GrainId);
        var targetReference = BindMessageTarget(entry);

        await disposableCache.DisposeAsync();

        AssertInvalidEntry(entry);
        AssertEventuallyCollected(targetReference);
    }

    [Fact]
    public void CreateGrainDirectoryCache_HighCardinalityHandlesRemainBoundedAndReleaseObjectGraphs()
    {
        const int cacheSize = 64;
        const int entryCount = 5_000;
        var (handles, targets) = CreateHighCardinalityHandles(cacheSize, entryCount);

        Assert.Equal(entryCount, handles.Length);
        Assert.Equal(entryCount, targets.Length);
        AssertEventuallyCollected(handles);
        AssertEventuallyCollected(targets);
        GC.KeepAlive(handles);
        GC.KeepAlive(targets);
    }

    private static (IGrainDirectoryCache Cache, IGrainDirectoryCacheEntrySource EntrySource) CreateEntryCache(
        int cacheSize = 10,
        TimeSpan? timeToLive = null,
        FakeTimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        if (timeProvider is not null)
        {
            services.AddKeyedSingleton<TimeProvider>(TimeProviderNames.GrainDirectory, timeProvider);
        }

        var cache = GrainDirectoryCacheFactory.CreateGrainDirectoryCache(
            services.BuildServiceProvider(),
            new GrainDirectoryOptions
            {
                CacheSize = cacheSize,
                MaximumCacheTTL = timeToLive ?? TimeSpan.FromHours(1)
            });

        return (cache, Assert.IsAssignableFrom<IGrainDirectoryCacheEntrySource>(cache));
    }

    private static GrainDirectoryCacheEntry GetEntry(IGrainDirectoryCacheEntrySource entrySource, GrainId grainId)
    {
        Assert.True(entrySource.TryGetEntry(grainId, out var entry));
        return Assert.IsType<GrainDirectoryCacheEntry>(entry);
    }

    private static void AssertMessageTarget(GrainDirectoryCacheEntry entry, IGrainContext expected)
    {
        Assert.True(entry.IsValid);
        Assert.True(entry.TryGetMessageTarget(out var actual));
        Assert.Same(expected, actual);
    }

    private static void AssertInvalidEntry(GrainDirectoryCacheEntry entry)
    {
        Assert.False(entry.IsValid);
        Assert.False(entry.TryGetMessageTarget(out var actual));
        Assert.Null(actual);
        Assert.False(entry.TrySetMessageTarget(CreateMessageTarget(), entry.Address));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference<IGrainContext> BindMessageTarget(GrainDirectoryCacheEntry entry)
    {
        var target = CreateMessageTarget();
        Assert.True(entry.TrySetMessageTarget(target, entry.Address));
        return new WeakReference<IGrainContext>(target);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference<GrainDirectoryCacheEntry> AddRemoveAndGetEntryHandle(
        IGrainDirectoryCache cache,
        IGrainDirectoryCacheEntrySource entrySource,
        GrainAddress address)
    {
        cache.AddOrUpdate(address, version: 1);
        var entry = GetEntry(entrySource, address.GrainId);
        var handle = entry.ReferenceHandle;
        Assert.True(cache.Remove(address.GrainId));
        Assert.False(handle.TryGetTarget(out var retained) && retained.IsValid);
        return handle;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference<GrainDirectoryCacheEntry> CreateRemovedEntryHandle()
    {
        var (cache, entrySource) = CreateEntryCache();
        var disposableCache = Assert.IsAssignableFrom<IAsyncDisposable>(cache);
        try
        {
            return AddRemoveAndGetEntryHandle(
                cache,
                entrySource,
                CreateGrainAddress(CreateGrainId(), port: 11111));
        }
        finally
        {
            disposableCache.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (
        WeakReference<GrainDirectoryCacheEntry>[] Handles,
        WeakReference<object>[] Targets) CreateHighCardinalityHandles(int cacheSize, int entryCount)
    {
        var (cache, entrySource) = CreateEntryCache(cacheSize);
        var disposableCache = Assert.IsAssignableFrom<IAsyncDisposable>(cache);
        var handles = new WeakReference<GrainDirectoryCacheEntry>[entryCount];
        var targets = new WeakReference<object>[entryCount];
        try
        {
            for (var i = 0; i < entryCount; i++)
            {
                var address = CreateGrainAddress(CreateGrainId(), port: 11111 + (i % 100));
                cache.AddOrUpdate(address, version: i);
                var entry = GetEntry(entrySource, address.GrainId);
                var target = new object();
                Assert.True(entry.TrySetMessageTarget(target, address));
                handles[i] = entry.ReferenceHandle;
                targets[i] = new(target);
            }

            Assert.InRange(cache.KeyValues.Count(), 0, cacheSize);
            return (handles, targets);
        }
        finally
        {
            disposableCache.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertEventuallyCollected(WeakReference<IGrainContext> targetReference)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            ForceFullCollection();

            if (!targetReference.TryGetTarget(out _))
            {
                return;
            }
        }

        Assert.False(targetReference.TryGetTarget(out _));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertEventuallyCollected(WeakReference<GrainDirectoryCacheEntry> entryReference)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            ForceFullCollection();

            if (!entryReference.TryGetTarget(out _))
            {
                return;
            }
        }

        Assert.False(entryReference.TryGetTarget(out _));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertEventuallyCollected(WeakReference<GrainDirectoryCacheEntry>[] entryReferences)
    {
        CollectGarbage();
        Assert.All(entryReferences, entryReference => Assert.False(entryReference.TryGetTarget(out _)));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertEventuallyCollected(WeakReference<object>[] targetReferences)
    {
        CollectGarbage();
        Assert.All(targetReferences, targetReference => Assert.False(targetReference.TryGetTarget(out _)));
    }

    private static void CollectGarbage()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            ForceFullCollection();
        }
    }

    private static void ForceFullCollection()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static GrainId CreateGrainId() => GrainId.Parse($"user/{Guid.NewGuid():N}");

    private static IGrainContext CreateMessageTarget() => DispatchProxy.Create<IGrainContext, GrainContextProxy>();

    private class GrainContextProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.ReturnType.IsValueType == true ? Activator.CreateInstance(targetMethod.ReturnType) : null;
    }

    private static GrainAddress CreateGrainAddress(GrainId grainId, int port) => new()
    {
        ActivationId = ActivationId.NewId(),
        GrainId = grainId,
        SiloAddress = SiloAddress.FromParsableString($"127.0.0.1:{port}@1"),
        MembershipVersion = new MembershipVersion(1)
    };
}
