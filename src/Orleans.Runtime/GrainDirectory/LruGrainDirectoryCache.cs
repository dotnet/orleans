using System;
using System.Collections.Generic;
using Orleans.Caching;

namespace Orleans.Runtime.GrainDirectory;

internal sealed class LruGrainDirectoryCache(int maxCacheSize) : ConcurrentLruCache<GrainId, (GrainAddress ActivationAddress, int Version)>(capacity: maxCacheSize), IGrainDirectoryCache
{
    private static readonly Func<(GrainAddress Address, int Version), GrainAddress, bool> ActivationAddressesMatch = (value, state) => GrainAddress.MatchesGrainIdAndSilo(state, value.Address);

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
}
