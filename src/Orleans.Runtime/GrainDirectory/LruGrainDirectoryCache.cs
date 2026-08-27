using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Orleans.Caching;

namespace Orleans.Runtime.GrainDirectory;

internal sealed class LruGrainDirectoryCache : ConcurrentLruCache<GrainId, GrainDirectoryCacheEntry>, IGrainDirectoryCache, IGrainDirectoryCacheEntrySource, IAsyncDisposable
{
    private static readonly Func<GrainDirectoryCacheEntry, GrainAddress, bool> ActivationAddressesMatch = (value, state) => GrainAddress.MatchesGrainIdAndSilo(state, value.Address);
    private readonly IDisposable _cacheSizeRegistration;

    public LruGrainDirectoryCache(
        int maxCacheSize,
        TimeSpan maxCacheTTL,
        TimeProvider timeProvider,
        DirectoryInstruments? directoryInstruments = null)
        : base(
            capacity: maxCacheSize,
            comparer: null,
            timeToLive: maxCacheTTL,
            timeProvider: timeProvider)
    {
        _cacheSizeRegistration = directoryInstruments is null
            ? NoOpDisposable.Instance
            : directoryInstruments.RegisterCacheSizeObserve(() => Count);
    }

    public void AddOrUpdate(GrainAddress activationAddress, int version) => AddOrUpdate(activationAddress.GrainId, new GrainDirectoryCacheEntry(activationAddress, version));

    public bool Remove(GrainId key) => TryRemove(key);

    public bool Remove(GrainAddress grainAddress) => TryRemove(grainAddress.GrainId, ActivationAddressesMatch, grainAddress);

    public bool LookUp(GrainId key, [NotNullWhen(true)] out GrainAddress? result, out int version)
    {
        if (TryGetEntry(key, out var entry))
        {
            version = entry.Version;
            result = entry.Address;
            return true;
        }

        version = default;
        result = default;
        return false;
    }

    public IEnumerable<(GrainAddress ActivationAddress, int Version)> KeyValues
    {
        get
        {
            foreach (var entry in this)
            {
                if (entry.Value.IsValid)
                {
                    yield return (entry.Value.Address, entry.Value.Version);
                }
            }
        }
    }

    public bool TryGetEntry(GrainId key, [NotNullWhen(true)] out GrainDirectoryCacheEntry? entry)
    {
        if (TryGet(key, out entry) && entry.IsValid)
        {
            return true;
        }

        entry = null;
        return false;
    }

    public new async ValueTask DisposeAsync()
    {
        _cacheSizeRegistration.Dispose();
        await base.DisposeAsync();
    }

    async ValueTask IAsyncDisposable.DisposeAsync() => await DisposeAsync();

    private sealed class NoOpDisposable : IDisposable
    {
        public static readonly NoOpDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
