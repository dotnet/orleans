using System;
using System.Collections.Generic;


namespace Orleans.Runtime.GrainDirectory
{
    internal class LRUBasedGrainDirectoryCache : IGrainDirectoryCache
    {
        private readonly LRU<GrainId, IReadOnlyList<Tuple<SiloAddress, ActivationId>>> cache;

        public LRUBasedGrainDirectoryCache(int maxCacheSize, TimeSpan maxEntryAge)
        {
            cache = new LRU<GrainId, IReadOnlyList<Tuple<SiloAddress, ActivationId>>>(maxCacheSize, maxEntryAge, null);
        }

        public void AddOrUpdate(GrainId key, IReadOnlyList<Tuple<SiloAddress, ActivationId>> value, int version)
        {
            // ignore the version number
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
                IEnumerator<KeyValuePair<GrainId, IReadOnlyList<Tuple<SiloAddress, ActivationId>>>> enumerator = cache.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    var current = enumerator.Current;
                    result.Add(new Tuple<GrainId, IReadOnlyList<Tuple<SiloAddress, ActivationId>>, int>(current.Key, current.Value, -1));
                }
                return result;
            }
        }
    }
}
