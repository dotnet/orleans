using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Orleans.Caching;

namespace Orleans.Runtime.GrainDirectory;

internal sealed class LruGrainDirectoryCache : ConcurrentLruCache<GrainId, (GrainAddress ActivationAddress, int Version)>, IGrainDirectoryCache, IGrainDirectoryCacheEntrySource, IAsyncDisposable
{
    private static readonly Func<(GrainAddress Address, int Version), GrainAddress, bool> ActivationAddressesMatch = (value, state) => GrainAddress.MatchesGrainIdAndSilo(state, value.Address);
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

    public void AddOrUpdate(GrainAddress activationAddress, int version) => AddOrUpdate(activationAddress.GrainId, (activationAddress, version));

    public bool Remove(GrainId key) => TryRemove(key);

    public bool Remove(GrainAddress grainAddress) => TryRemove(grainAddress.GrainId, ActivationAddressesMatch, grainAddress);

    public bool LookUp(GrainId key, [NotNullWhen(true)] out GrainAddress? result, out int version)
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

    public bool TryGetEntry(GrainId key, [NotNullWhen(true)] out GrainDirectoryCacheEntry? entry)
    {
        if (TryGetItem(key, out var item)
            && item is GrainDirectoryCacheEntry result
            && result.IsValid)
        {
            entry = result;
            return true;
        }

        entry = null;
        return false;
    }

    protected override LruItem CreateItem(GrainId key, (GrainAddress ActivationAddress, int Version) value)
        => new GrainDirectoryCacheEntry(this, key, value, GetCurrentTimestamp());

    internal void Touch(GrainDirectoryCacheEntry entry) => TouchItem(entry);

    protected override void UpdateItem(LruItem item, (GrainAddress ActivationAddress, int Version) value)
        => ((GrainDirectoryCacheEntry)item).Update(value);

    protected override void OnItemRemoved(LruItem item)
        => ((GrainDirectoryCacheEntry)item).Invalidate();

    public new async ValueTask DisposeAsync()
    {
        _cacheSizeRegistration.Dispose();
        Clear();
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
