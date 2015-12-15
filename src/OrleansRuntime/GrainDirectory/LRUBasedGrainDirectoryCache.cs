using System;
using System.Collections.Generic;


namespace Orleans.Runtime.GrainDirectory
{
    internal class LRUBasedGrainDirectoryCache<TValue> : IGrainDirectoryCache<TValue>
    {
        private readonly LRU<GrainId, TValue> cache;

        public LRUBasedGrainDirectoryCache(int maxCacheSize, TimeSpan maxEntryAge)
        {
            cache = new LRU<GrainId, TValue>(maxCacheSize, maxEntryAge, null);
        }

        public void AddOrUpdate(GrainId key, TValue value, int version)
        {
            // ignore the version number
            cache.Add(key, value);
        }

        public bool Remove(GrainId key)
        {
            TValue tmp;
            return cache.RemoveKey(key, out tmp);
        }

        public void Clear()
        {
            cache.Clear();
        }

        public bool LookUp(GrainId key, out TValue result, out int version)
        {
            version = default(int);
            return cache.TryGetValue(key, out result);
        }

        public IReadOnlyList<Tuple<GrainId, TValue, int>> KeyValues
        {
            get
            {
                var result = new List<Tuple<GrainId, TValue, int>>();
                IEnumerator<KeyValuePair<GrainId, TValue>> enumerator = cache.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    var current = enumerator.Current;
                    result.Add(new Tuple<GrainId, TValue, int>(current.Key, current.Value, -1));
                }
                return result;
            }
        }
    }
}
