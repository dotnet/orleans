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
        // Delegate type for fetching the value associated with a given key.
        public delegate TValue FetchValueDelegate(TKey key);

        // The following machinery is used to notify client objects when a key and its value 
        // is being flushed from the cache.
        // The client's event handler is called after the key has been removed from the cache,
        // but when the cache is in a consistent state so that other methods on the cache may freely
        // be invoked.
        public class FlushEventArgs : EventArgs
        {
            private readonly TKey key;
            private readonly TValue value;

            public FlushEventArgs(TKey k, TValue v)
            {
                key = k;
                value = v;
            }

            public TKey Key
            {
                get { return key; }
            }

            public TValue Value
            {
                get { return value; }
            }
        }

        public event EventHandler<FlushEventArgs> RaiseFlushEvent;

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
        private readonly ConcurrentDictionaryWithCount<TKey, TimestampedValue> cache;
        readonly FetchValueDelegate fetcher;

        public int Count { get { return cache.Count; } }
        public int MaximumSize { get; private set; }

        /// <summary>
        /// Creates a new LRU cache.
        /// </summary>
        /// <param name="maxSize">Maximum number of entries to allow.</param>
        /// <param name="maxAge">Maximum age of an entry.</param>
        /// <param name="f"></param>
        public LRU(int maxSize, TimeSpan maxAge, FetchValueDelegate f)
        {
            if (maxSize <= 0)
            {
                throw new ArgumentOutOfRangeException("maxSize", "LRU maxSize must be greater than 0");
            }
            MaximumSize = maxSize;
            requiredFreshness = maxAge;
            fetcher = f;
            cache = new ConcurrentDictionaryWithCount<TKey, TimestampedValue>();
        }

        public void Add(TKey key, TValue value)
        {
            AdjustSize();
            var result = new TimestampedValue(this, value);
            cache[key] = result;
        }

        public bool ContainsKey(TKey key) => cache.ContainsKey(key);

        public bool RemoveKey(TKey key, out TValue value)
        {
            value = default(TValue);
            if (!cache.TryRemove(key, out var tv)) return false;

            value = tv.Value;
            return true;
        }

        public void Clear()
        {
            foreach (var pair in cache)
            {
                var args = new FlushEventArgs(pair.Key, pair.Value.Value);
                EventHandler<FlushEventArgs> handler = RaiseFlushEvent;
                if (handler == null) continue;

                handler(this, args);
            }
            cache.Clear();
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            TimestampedValue result;

            value = default(TValue);

            if (cache.TryGetValue(key, out result))
            {
                var age = DateTime.UtcNow.Subtract(result.WhenLoaded);
                if (age > requiredFreshness)
                {
                    if (!cache.TryRemove(key, out result)) return false;
                    if (RaiseFlushEvent == null) return false;

                    var args = new FlushEventArgs(key, result.Value);
                    RaiseFlushEvent(this, args);
                    return false;
                }
                else
                {
                    result.Generation = Interlocked.Increment(ref nextGeneration);
                    value = result.Value;
                    return true;
                }
            }
            else
            {
                return false;
            }
        }

        public TValue Get(TKey key)
        {
            if (TryGetValue(key, out var value)) return value;
            if (fetcher == null) return value;

            value = fetcher(key);
            Add(key, value);
            return value;
        }

        /// <summary>
        /// Remove all expired value from the LRU instance.
        /// </summary>
        public void RemoveExpired()
        {
            var now = DateTime.UtcNow;
            var toRemove = new List<TKey>();
            foreach (var entry in this.cache)
            {
                var age = DateTime.UtcNow.Subtract(entry.Value.WhenLoaded);
                if (age > requiredFreshness)
                {
                    toRemove.Add(entry.Key);
                }
            }
            foreach (var key in toRemove)
            {
                if (cache.TryRemove(key, out var result) && RaiseFlushEvent != null)
                {
                    var args = new FlushEventArgs(key, result.Value);
                    RaiseFlushEvent(this, args);
                }
            }
        }

        private void AdjustSize()
        {
            while (cache.Count >= MaximumSize)
            {
                long generationToDelete = Interlocked.Increment(ref generationToFree);
                KeyValuePair<TKey, TimestampedValue> entryToFree =
                    cache.FirstOrDefault(kvp => kvp.Value.Generation == generationToDelete);

                if (entryToFree.Key == null) continue;
                TKey keyToFree = entryToFree.Key;
                if (!cache.TryRemove(keyToFree, out var old)) continue;
                if (RaiseFlushEvent == null) continue;

                var args = new FlushEventArgs(keyToFree, old.Value);
                RaiseFlushEvent(this, args);
            }
        }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            return cache.Select(p => new KeyValuePair<TKey, TValue>(p.Key, p.Value.Value)).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        private class ConcurrentDictionaryWithCount<K, V> : IEnumerable<KeyValuePair<K, V>>
        {
            private int count;

            private ConcurrentDictionary<K, V> dictionary;


            public ConcurrentDictionaryWithCount()
            {
                dictionary = new ConcurrentDictionary<K, V>();
            }

            public int Count => count;

            public V this[K key] 
            { 
                get => dictionary[key]; 
                set 
                {
                    // if the value is to be added, increment count, otherwise just replace
                    dictionary.AddOrUpdate(key, k => { Interlocked.Increment(ref count); return value; }, (k, _) => value);
                }
            }

            public bool TryRemove(K key, out V value)
            {
                if (dictionary.TryRemove(key, out value))
                {
                    Interlocked.Decrement(ref count);
                    return true;
                }
                else
                {
                    return false;
                }
            }

            public bool ContainsKey(K key)
            {
                return dictionary.ContainsKey(key);
            }

            public IEnumerator<KeyValuePair<K, V>> GetEnumerator()
            {
                return dictionary.GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return dictionary.GetEnumerator();
            }

            public bool TryGetValue(K key, out V value)
            {
                return dictionary.TryGetValue(key, out value);
            }

            // not thread-safe: if anything is added, or even removed after addition, between Clear and Count, count may be off
            public void Clear()
            {
                dictionary.Clear();
                count = dictionary.Count;
            }
        }
    }
}
