using System;
using System.Collections.Generic;


namespace Orleans.Runtime.GrainDirectory
{
    internal class LRUBasedGrainDirectoryCache : IGrainDirectoryCache
    {
        private readonly LRU<GrainId, ActivationAddress> cache;

        public LRUBasedGrainDirectoryCache(int maxCacheSize, TimeSpan maxEntryAge) => cache = new(maxCacheSize, maxEntryAge);

        public void AddOrUpdate(ActivationAddress activationAddress, int version)
        {
            // ignore the version number
            cache.Add(activationAddress.Grain, activationAddress);
        }

        public bool Remove(GrainId key) => cache.RemoveKey(key);

        public void Clear() => cache.Clear();

        public bool LookUp(GrainId key, out ActivationAddress result, out int version)
        {
            version = default;
            return cache.TryGetValue(key, out result);
        }

        public IEnumerable<(ActivationAddress ActivationAddress, int Version)> KeyValues
        {
            get
            {
                foreach (var entry in cache)
                {
                    yield return (entry.Value, -1);
                }
            }
        }
    }
}
