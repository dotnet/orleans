using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Orleans.Runtime
{
    // This class implements an LRU cache of values. It keeps a bounded set of values and will
    // flush "old" values 
    internal class LRU<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>
    {
        // The following machinery is used to notify client objects when a key and its value 
        // is being flushed from the cache.
        // The client's event handler is called after the key has been removed from the cache,
        // but when the cache is in a consistent state so that other methods on the cache may freely
        // be invoked.
        public event Action RaiseFlushEvent;

        private long nextGeneration = 0;
        private long generationToFree = 0;
        private readonly TimeSpan requiredFreshness;
        // We want this to be a reference type so that we can update the values in the cache
        // without having to call AddOrUpdate, which is a nuisance
        private class TimestampedValue
        {
            public readonly DateTime WhenLoaded;
            public readonly TValue Value;
            public long Generation;

            public TimestampedValue(LRU<TKey, TValue> l, TValue v)
            {
                Generation = Interlocked.Increment(ref l.nextGeneration);
                Value = v;
                WhenLoaded = DateTime.UtcNow;
            }
        }
        private readonly ConcurrentDictionary<TKey, TimestampedValue> cache = new();
        private int count;

        public int Count => count;
        public int MaximumSize { get; }

        /// <summary>
        /// Creates a new LRU cache.
        /// </summary>
        /// <param name="maxSize">Maximum number of entries to allow.</param>
        /// <param name="maxAge">Maximum age of an entry.</param>
        public LRU(int maxSize, TimeSpan maxAge)
        {
            if (maxSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxSize), "LRU maxSize must be greater than 0");
            }
            MaximumSize = maxSize;
            requiredFreshness = maxAge;
        }

        public void Add(TKey key, TValue value)
        {
            var result = new TimestampedValue(this, value);
            AdjustSize();

            // add/update delegates can be called multiple times, but only the last result counts
            var added = false;
            cache.AddOrUpdate(key, _ =>
            {
                added = true;
                return result;
            }, (_, old) =>
            {
                added = false;
                // if multiple values are added at once for the same key, take the newest one
                return old.WhenLoaded >= result.WhenLoaded && old.Generation > result.Generation ? old : result;
            });

            if (added) Interlocked.Increment(ref count);
        }

        public bool ContainsKey(TKey key) => cache.ContainsKey(key);

        public bool RemoveKey(TKey key)
        {
            if (!cache.TryRemove(key, out _)) return false;

            Interlocked.Decrement(ref count);
            return true;
        }

        public void Clear()
        {
            if (RaiseFlushEvent is { } FlushEvent)
            {
                foreach (var _ in cache) FlushEvent();
            }

            // not thread-safe: if anything is added, or even removed after addition, between Clear and Count, count may be off
            cache.Clear();
            Interlocked.Exchange(ref count, 0);
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            if (cache.TryGetValue(key, out var result))
            {
                var age = DateTime.UtcNow.Subtract(result.WhenLoaded);
                if (age > requiredFreshness)
                {
                    if (RemoveKey(key)) RaiseFlushEvent?.Invoke();
                }
                else
                {
                    result.Generation = Interlocked.Increment(ref nextGeneration);
                    value = result.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        public TValue Get(TKey key)
        {
            TryGetValue(key, out var value);
            return value;
        }

        /// <summary>
        /// Remove all expired value from the LRU instance.
        /// </summary>
        public void RemoveExpired()
        {
            var frestTime = DateTime.UtcNow - requiredFreshness;
            foreach (var entry in this.cache)
            {
                if (entry.Value.WhenLoaded < frestTime)
                {
                    if (RemoveKey(entry.Key)) RaiseFlushEvent?.Invoke();
                }
            }
        }

        private void AdjustSize()
        {
            while (Count >= MaximumSize)
            {
                long generationToDelete = Interlocked.Increment(ref generationToFree);
                foreach (var e in cache)
                {
                    if (e.Value.Generation <= generationToDelete)
                    {
                        if (RemoveKey(e.Key)) RaiseFlushEvent?.Invoke();
                        break;
                    }
                }
            }
        }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            return cache.Select(p => new KeyValuePair<TKey, TValue>(p.Key, p.Value.Value)).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
