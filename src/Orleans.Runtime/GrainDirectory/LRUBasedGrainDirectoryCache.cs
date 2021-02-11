using System;
using System.Collections.Generic;


namespace Orleans.Runtime.GrainDirectory
{
    internal class LRUBasedGrainDirectoryCache : IGrainDirectoryCache
    {
        private readonly LRU<GrainId, IReadOnlyList<Tuple<SiloAddress, ActivationId>>> cache;

        public LRUBasedGrainDirectoryCache(int maxCacheSize, TimeSpan maxEntryAge) => cache = new(maxCacheSize, maxEntryAge);

        public void AddOrUpdate(GrainId key, IReadOnlyList<Tuple<SiloAddress, ActivationId>> value, int version)
        {
            // ignore the version number
            cache.Add(key, value);
        }

        public bool Remove(GrainId key) => cache.RemoveKey(key);

        public void Clear() => cache.Clear();

        public bool LookUp(GrainId key, out IReadOnlyList<Tuple<SiloAddress, ActivationId>> result, out int version)
        {
            version = default(int);
            return cache.TryGetValue(key, out result);
        }

        public IReadOnlyList<Tuple<GrainId, IReadOnlyList<Tuple<SiloAddress, ActivationId>>, int>> KeyValues
        {
            get
            {
                var result = new List<Tuple<GrainId, IReadOnlyList<Tuple<SiloAddress, ActivationId>>, int>>();
                var enumerator = cache.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    var current = enumerator.Current;
                    result.Add(new(current.Key, current.Value, -1));
                }
                return result;
            }
        }
    }
}
