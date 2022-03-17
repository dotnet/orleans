using System;
using System.Collections.Generic;
using Orleans.GrainDirectory;

namespace Orleans.Runtime.GrainDirectory
{
    internal class LRUBasedGrainDirectoryCache : IGrainDirectoryCache
    {
        private readonly LRU<GrainId, (IReadOnlyList<Tuple<SiloAddress, ActivationId>> Entry, int Version)> cache;

        public LRUBasedGrainDirectoryCache(int maxCacheSize, TimeSpan maxEntryAge)
        {
            cache = new (maxCacheSize, maxEntryAge, null);
        }

        public void AddOrUpdate(GrainId key, IReadOnlyList<Tuple<SiloAddress, ActivationId>> value, int version)
        {
            // ignore the version number
            cache.Add(key, (value, version));
        }

        public bool Remove(GrainId key) => cache.RemoveKey(key, out var tmp);

        public bool Remove(ActivationAddress activationAddress)
        {
            return cache.TryRemove(activationAddress.Grain, ActivationAddressEqual, activationAddress.Activation);

            static bool ActivationAddressEqual(ActivationId activationId, (IReadOnlyList<Tuple<SiloAddress, ActivationId>> Entry, int Version) value)
            {
                // value.Entry.Count should always be == 1, but to be safe ask to remove the entry if the count is zero
                if (value.Entry.Count == 0 || activationId.Equals(value.Entry[0].Item2))
                {
                    return true;
                }
                return false;
            }
        }

        public void Clear()
        {
            cache.Clear();
        }

        public bool LookUp(GrainId key, out IReadOnlyList<Tuple<SiloAddress, ActivationId>> result, out int version)
        {
            if (cache.TryGetValue(key, out var value))
            {
                result = value.Entry;
                version = value.Version;
                return true;
            }

            result = default;
            version = default;
            return false;
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
                    result.Add(new Tuple<GrainId, IReadOnlyList<Tuple<SiloAddress, ActivationId>>, int>(current.Key, current.Value.Entry, current.Value.Version));
                }
                return result;
            }
        }
    }
}
