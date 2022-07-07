using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Orleans.Runtime
{
    // This class implements an LRU (Least Recently Used) cache of values. It keeps a bounded set of values and will
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
        private class TimestampedValue : IEquatable<TimestampedValue>
        {
            public readonly TValue Value;
            public CoarseStopwatch Age;
            public long Generation;

            public TimestampedValue(TValue v, long generation)
            {
                Generation = generation;
                Value = v;
                Age = CoarseStopwatch.StartNew();
            }

            public override bool Equals(object obj) => obj is TimestampedValue value && Equals(value);
            public bool Equals(TimestampedValue other) => ReferenceEquals(this, other) || Generation == other.Generation && EqualityComparer<TValue>.Default.Equals(Value, other.Value);
            public override int GetHashCode() => HashCode.Combine(Value, Generation);
        }

        private readonly ConcurrentDictionary<TKey, TimestampedValue> cache = new();
        private int count;

        public int Count => count;
        public int MaximumSize { get; }

        /// <summary>
        /// Creates a new LRU (Least Recently Used) cache.
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

        public TValue GetOrAdd<TState>(TKey key, Func<TState, TKey, TValue> addFunc, TState state)
        {
            var generation = GetNewGeneration();
            var storedValue = cache.AddOrUpdate(
                key,
                static (key, state) =>
                {
                    var (_, outerState, addFunc, generation) = state;
                    return new TimestampedValue(addFunc(outerState, key), generation);
                },
                static (key, existing, state) =>
                {
                    var (self, _, _, _) = state;
                    existing.Age.Restart();
                    existing.Generation = self.GetNewGeneration();
                    return existing;
                },
                (Self: this, State: state, AddFunc: addFunc, Generation: generation));

            var result = storedValue.Value;

            if (storedValue.Generation == generation)
            {
                Interlocked.Increment(ref count);
                AdjustSize();
            }

            return result;
        }

        public void Add(TKey key, TValue value)
        {
            GetOrAdd(key, static (value, key) => value, value);
        }

        public bool ContainsKey(TKey key) => cache.ContainsKey(key);

        public bool RemoveKey(TKey key)
        {
            if (!cache.TryRemove(key, out _)) return false;

            Interlocked.Decrement(ref count);
            return true;
        }

        public bool TryRemove<T>(TKey key, Func<T, TValue, bool> predicate, T context)
        {
            if (!cache.TryGetValue(key, out var timestampedValue))
            {
                return false;
            }

            if (predicate(context, timestampedValue.Value) && TryRemoveInternal(key, timestampedValue))
            {
                Interlocked.Decrement(ref count);
                return true;
            }

            return false;

            bool TryRemoveInternal(TKey key, TimestampedValue value)
            {
                var entry = new KeyValuePair<TKey, TimestampedValue>(key, value);

#if NET5_0_OR_GREATER
                return cache.TryRemove(entry);
#else
            // Cast the dictionary to its interface type to access the explicitly implemented Remove method.
            var cacheDictionary = (IDictionary<TKey, TimestampedValue>)cache;
            return cacheDictionary.Remove(entry);
#endif
            }
        }

        private long GetNewGeneration() => Interlocked.Increment(ref nextGeneration);

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
                var age = result.Age.Elapsed;
                if (age > requiredFreshness)
                {
                    if (RemoveKey(key)) RaiseFlushEvent?.Invoke();
                }
                else
                {
                    result.Age.Restart();
                    result.Generation = GetNewGeneration();
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
        /// Remove all expired values from the LRU (Least Recently Used) instance.
        /// </summary>
        public void RemoveExpired()
        {
            foreach (var entry in this.cache)
            {
                if (entry.Value.Age.Elapsed > requiredFreshness)
                {
                    if (RemoveKey(entry.Key)) RaiseFlushEvent?.Invoke();
                }
            }
        }

        private void AdjustSize()
        {
            if (Count <= MaximumSize)
            {
                return;
            }

            RemoveExpired();

            var minGeneration = long.MaxValue;
            while (Count > MaximumSize)
            {
                var targetGeneration = Interlocked.Increment(ref generationToFree);

                foreach (var e in cache)
                {
                    var entryGeneration = e.Value.Generation;
                    if (minGeneration > entryGeneration)
                    {
                        minGeneration = entryGeneration;
                    }

                    if (entryGeneration <= targetGeneration)
                    {
                        if (RemoveKey(e.Key)) RaiseFlushEvent?.Invoke();
                    }
                }

                // Skip forward to the minimum present generation.
                var diff = minGeneration - generationToFree - 1;
                if (minGeneration < long.MaxValue && diff > 0)
                {
                    Interlocked.Add(ref generationToFree, diff);
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
