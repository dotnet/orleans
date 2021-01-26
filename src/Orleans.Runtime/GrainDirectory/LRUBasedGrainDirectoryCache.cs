using System;
using System.Collections.Generic;

namespace Orleans.Runtime.GrainDirectory
{
    internal class LRUBasedGrainDirectoryCache : IGrainDirectoryCache
    {
        private readonly LRU<GrainId, (SiloAddress SiloAddress, ActivationId ActivationId, int VersionTag)> cache;

        public LRUBasedGrainDirectoryCache(int maxCacheSize, TimeSpan maxEntryAge)
        {
            cache = new LRU<GrainId, (SiloAddress, ActivationId, int)>(maxCacheSize, maxEntryAge, null);
        }

        public void AddOrUpdate(GrainId key, (SiloAddress SiloAddress, ActivationId ActivationId, int VersionTag) value)
        {
            cache.Add(key, value);
        }

        public bool Remove(GrainId key)
        {
            return cache.RemoveKey(key, out _);
        }

        public void Clear()
        {
            cache.Clear();
        }

        public bool LookUp(GrainId key, out (SiloAddress SiloAddress, ActivationId ActivationId, int VersionTag) result)
        {
            return cache.TryGetValue(key, out result);
        }

        public List<(GrainId, SiloAddress, ActivationId, int)> KeyValues
        {
            get
            {
                var result = new List<(GrainId, SiloAddress, ActivationId, int)>();
                var enumerator = cache.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    var current = enumerator.Current;
                    var value = current.Value;
                    result.Add((current.Key, value.SiloAddress, value.ActivationId, value.VersionTag));
                }
                return result;
            }
        }
    }
}
