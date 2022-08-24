using System;
using System.Collections.Generic;


namespace Orleans.Runtime.GrainDirectory
{
    internal class LRUBasedGrainDirectoryCache : IGrainDirectoryCache
    {
        private static readonly Func<GrainAddress, (GrainAddress Address, int Version), bool> ActivationAddressesMatch = (a, b) => a.Matches(b.Address);
        private readonly LRU<GrainId, (GrainAddress ActivationAddress, int Version)> cache;

        public LRUBasedGrainDirectoryCache(int maxCacheSize, TimeSpan maxEntryAge) => cache = new(maxCacheSize, maxEntryAge);

        public void AddOrUpdate(GrainAddress activationAddress, int version)
        {
            // ignore the version number
            cache.Add(activationAddress.GrainId, (activationAddress, version));
        }

        public bool Remove(GrainId key) => cache.RemoveKey(key);

        public bool Remove(GrainAddress grainAddress) => cache.TryRemove(grainAddress.GrainId, ActivationAddressesMatch, grainAddress);

        public void Clear() => cache.Clear();

        public bool LookUp(GrainId key, out GrainAddress result, out int version)
        {
            if (cache.TryGetValue(key, out var entry))
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
                foreach (var entry in cache)
                {
                    yield return (entry.Value.ActivationAddress, entry.Value.Version);
                }
            }
        }
    }
}
