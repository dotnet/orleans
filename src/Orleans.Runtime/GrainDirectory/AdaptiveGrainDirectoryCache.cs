using System;
using System.Collections.Generic;
using System.Text;
using Orleans.Internal;
using Orleans.Statistics;

namespace Orleans.Runtime.GrainDirectory
{
    internal class AdaptiveGrainDirectoryCache : IGrainDirectoryCache
    {
        internal class GrainDirectoryCacheEntry
        {
            internal GrainAddress Address { get; }
            private CoarseStopwatch LastRefreshed { get; set; }
            internal TimeSpan ExpirationTimer { get; private set; }
            internal int ETag { get; }

            /// <summary>
            /// flag notifying whether this cache entry was accessed lately
            /// (more precisely, since the last refresh)
            /// </summary>
            internal int NumAccesses { get; set; }

            internal GrainDirectoryCacheEntry(GrainAddress value, int etag, TimeSpan expirationTimer)
            {
                Address = value;
                ETag = etag;
                ExpirationTimer = expirationTimer;
                LastRefreshed = CoarseStopwatch.StartNew();
                NumAccesses = 0;
            }

            internal bool IsExpired()
            {
                return LastRefreshed.Elapsed >= ExpirationTimer;
            }

            internal void Refresh(TimeSpan newExpirationTimer)
            {
                LastRefreshed = CoarseStopwatch.StartNew();
                ExpirationTimer = newExpirationTimer;
            }
        }

        private static readonly Func<GrainAddress, GrainDirectoryCacheEntry, bool> ActivationAddressesMatches = (addr, entry) => addr.Matches(entry.Address);

        private readonly LRU<GrainId, GrainDirectoryCacheEntry> cache;
        /// controls the time the new entry is considered "fresh" (unit: ms)
        private readonly TimeSpan initialExpirationTimer;
        /// controls the exponential growth factor (i.e., x2, x4) for the freshness timer (unit: none)
        private readonly double exponentialTimerGrowth;
        // controls the boundary on the expiration timer
        private readonly TimeSpan maxExpirationTimer;

        internal long NumAccesses;   // number of cache item accesses (for stats)
        internal long NumHits;       // number of cache access hits (for stats)

        internal long LastNumAccesses;
        internal long LastNumHits;

        public AdaptiveGrainDirectoryCache(TimeSpan initialExpirationTimer, TimeSpan maxExpirationTimer, double exponentialTimerGrowth, int maxCacheSize)
        {
            cache = new(maxCacheSize, TimeSpan.MaxValue);

            this.initialExpirationTimer = initialExpirationTimer;
            this.maxExpirationTimer = maxExpirationTimer;
            this.exponentialTimerGrowth = exponentialTimerGrowth;

            DirectoryInstruments.RegisterCacheSizeObserve(() => cache.Count);
        }

        public void AddOrUpdate(GrainAddress value, int version)
        {
            var entry = new GrainDirectoryCacheEntry(value, version, initialExpirationTimer);

            // Notice that LRU should know how to throw the oldest entry if the cache is full
            cache.AddOrUpdate(value.GrainId, entry);
        }

        public bool Remove(GrainId key) => cache.RemoveKey(key);

        public bool Remove(GrainAddress key) => cache.TryRemove(key.GrainId, ActivationAddressesMatches, key);

        public void Clear() => cache.Clear();

        public bool LookUp(GrainId key, out GrainAddress result, out int version)
        {
            NumAccesses++;      // for stats

            // Here we do not check whether the found entry is expired.
            // It will be done by the thread managing the cache.
            // This is to avoid situation where the entry was just expired, but the manager still have not run and have not refereshed it.
            if (!cache.TryGetValue(key, out var tmp))
            {
                result = default;
                version = default;
                return false;
            }

            NumHits++;      // for stats
            tmp.NumAccesses++;
            result = tmp.Address;
            version = tmp.ETag;
            return true;
        }

        public IEnumerable<(GrainAddress ActivationAddress, int Version)> KeyValues
        {
            get
            {
                foreach (var value in cache)
                {
                    yield return (value.Value.Address, value.Value.ETag);
                }
            }
        }

        public bool MarkAsFresh(GrainId key)
        {
            GrainDirectoryCacheEntry result;
            if (!cache.TryGetValue(key, out result)) return false;

            TimeSpan newExpirationTimer = StandardExtensions.Min(maxExpirationTimer, result.ExpirationTimer.Multiply(exponentialTimerGrowth));
            result.Refresh(newExpirationTimer);

            return true;
        }

        internal GrainDirectoryCacheEntry Get(GrainId key)
        {
            return cache.Get(key);
        }

        internal IEnumerator<KeyValuePair<GrainId, GrainDirectoryCacheEntry>> GetStoredEntries()
        {
            return cache.GetEnumerator();
        }

        public override string ToString()
        {
            var sb = new StringBuilder();

            long curNumAccesses = NumAccesses - LastNumAccesses;
            LastNumAccesses = NumAccesses;
            long curNumHits = NumHits - LastNumHits;
            LastNumHits = NumHits;

            sb.Append("Adaptive cache statistics:").AppendLine();
            sb.AppendFormat("   Cache size: {0} entries ({1} maximum)", cache.Count, cache.MaximumSize).AppendLine();
            sb.AppendFormat("   Since last call:").AppendLine();
            sb.AppendFormat("      Accesses: {0}", curNumAccesses);
            sb.AppendFormat("      Hits: {0}", curNumHits);
            if (curNumAccesses > 0)
            {
                sb.AppendFormat("      Hit Rate: {0:F1}%", (100.0 * curNumHits) / curNumAccesses).AppendLine();
            }
            sb.AppendFormat("   Since start:").AppendLine();
            sb.AppendFormat("      Accesses: {0}", LastNumAccesses);
            sb.AppendFormat("      Hits: {0}", LastNumHits);
            if (LastNumAccesses > 0)
            {
                sb.AppendFormat("      Hit Rate: {0:F1}%", (100.0 * LastNumHits) / LastNumAccesses).AppendLine();
            }

            return sb.ToString();
        }
    }
}
