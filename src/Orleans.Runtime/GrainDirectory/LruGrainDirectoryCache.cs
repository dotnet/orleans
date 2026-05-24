using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Caching;

#nullable disable
namespace Orleans.Runtime.GrainDirectory;

internal sealed class LruGrainDirectoryCache : ConcurrentLruCache<GrainId, (GrainAddress ActivationAddress, int Version)>, IGrainDirectoryCache, IAsyncDisposable
{
    private static readonly Func<(GrainAddress Address, int Version), GrainAddress, bool> ActivationAddressesMatch = (value, state) => GrainAddress.MatchesGrainIdAndSilo(state, value.Address);
    private readonly IDisposable _cacheSizeRegistration;

    public LruGrainDirectoryCache(
        int maxCacheSize,
        TimeSpan maxCacheTTL,
        TimeProvider timeProvider)
        : base(
            capacity: maxCacheSize,
            comparer: null,
            timeToLive: maxCacheTTL,
            timeProvider: timeProvider)
    {
        _cacheSizeRegistration = DirectoryInstruments.RegisterCacheSizeObserve(() => Count);
    }

    public void AddOrUpdate(GrainAddress activationAddress, int version) => AddOrUpdate(activationAddress.GrainId, (activationAddress, version));

    public bool Remove(GrainId key) => TryRemove(key);

    public bool Remove(GrainAddress grainAddress) => TryRemove(grainAddress.GrainId, ActivationAddressesMatch, grainAddress);

    public bool LookUp(GrainId key, out GrainAddress result, out int version)
    {
        if (TryGet(key, out var entry))
        {
            version = entry.Version;
            result = entry.ActivationAddress;
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
                yield return (entry.Value.ActivationAddress, entry.Value.Version);
            }
        }
    }

    public new async ValueTask DisposeAsync()
    {
        _cacheSizeRegistration.Dispose();
        await base.DisposeAsync();
    }

    async ValueTask IAsyncDisposable.DisposeAsync() => await DisposeAsync();
}
