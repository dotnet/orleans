using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Orleans
{
    /// <summary>
    /// Constants used by the generic <see cref="Interner{K, T}"/> class.
    /// </summary>
    internal static class InternerConstants
    {
        /* Recommended cache sizes, based on expansion policy of ConcurrentDictionary
        // Internal implementation of ConcurrentDictionary resizes to prime numbers (not divisible by 3 or 5 or 7)
        31
        67
        137
        277
        557
        1,117
        2,237
        4,477
        8,957
        17,917
        35,837
        71,677
        143,357
        286,717
        573,437
        1,146,877
        2,293,757
        4,587,517
        9,175,037
        18,350,077
        36,700,157
        */
        public const int SIZE_SMALL = 67;
        public const int SIZE_MEDIUM = 1117;
        public const int SIZE_LARGE = 143357;
        public const int SIZE_X_LARGE = 2293757;
    }

    /// <summary>
    /// Provide a weakly-referenced cache of interned objects.
    /// Interner is used to optimize garbage collection.
    /// We use it to store objects that are allocated frequently and may have long lifetime. 
    /// This means those object may quickly fill gen 2 and cause frequent costly full heap collections.
    /// Specifically, a message that arrives to a silo and all the headers and ids inside it may stay alive long enough to reach gen 2.
    /// Therefore, we store all ids in interner to re-use their memory across different messages.
    /// </summary>
    /// <typeparam name="TKey">Type of objects to be used for intern keys.</typeparam>
    /// <typeparam name="TValue">Type of objects to be interned.</typeparam>
    internal sealed class Interner<TKey, TValue> : IDisposable
        where TKey : IEquatable<TKey>
        where TValue : class
    {
        private readonly Timer cacheCleanupTimer;

        [NonSerialized]
        private readonly ConcurrentDictionary<TKey, WeakReference<TValue>> internCache;

        /// <summary>
        /// Initializes a new instance of the <see cref="Interner{K, T}"/> class.
        /// </summary>
        /// <param name="initialSize">The initial size of the interner mapping.</param>
        public Interner(int initialSize = InternerConstants.SIZE_SMALL)
        {
            int concurrencyLevel = Environment.ProcessorCount; // Default from ConcurrentDictionary class in .NET Core for size 31
            if (initialSize >= InternerConstants.SIZE_MEDIUM) concurrencyLevel *= 4;
            if (initialSize >= InternerConstants.SIZE_LARGE) concurrencyLevel *= 4;
            concurrencyLevel = Math.Min(concurrencyLevel, 1024);
            this.internCache = new ConcurrentDictionary<TKey, WeakReference<TValue>>(concurrencyLevel, initialSize);

            if (typeof(TKey) != typeof(TValue))
            {
                var period = TimeSpan.FromMinutes(10);
                var dueTime = period + TimeSpan.FromTicks(new Random().Next((int)TimeSpan.TicksPerMinute)); // add some initial jitter
                cacheCleanupTimer = new Timer(InternCacheCleanupTimerCallback, null, dueTime, period);
            }
        }

        /// <summary>
        /// Find cached copy of object with specified key, otherwise create new one using the supplied creator-function.
        /// </summary>
        /// <param name="key">key to find</param>
        /// <param name="creatorFunc">function to create new object and store for this key if no cached copy exists</param>
        /// <returns>Object with specified key - either previous cached copy or newly created</returns>
        public TValue FindOrCreate(TKey key, Func<TKey, TValue> creatorFunc)
        {
            // Attempt to get the existing value from cache.
            // If no cache entry exists, create and insert a new one using the creator function.
            if (!internCache.TryGetValue(key, out var cacheEntry))
            {
                var obj = creatorFunc(key);
                internCache[key] = new WeakReference<TValue>(obj);
                return obj;
            }

            // If a cache entry did exist, determine if it still holds a valid value.
            if (!cacheEntry.TryGetTarget(out var result))
            {
                // Create new object and ensure the entry is still valid by re-inserting it into the cache.
                var obj = creatorFunc(key);
                cacheEntry.SetTarget(obj);
                return obj;
            }

            return result;
        }

        /// <summary>
        /// Find cached copy of object with specified key, otherwise store the supplied one. 
        /// </summary>
        /// <param name="key">key to find</param>
        /// <param name="obj">The new object to store for this key if no cached copy exists</param>
        /// <returns>Object with specified key - either previous cached copy or just passed in</returns>
        public TValue Intern(TKey key, TValue obj)
        {
            // Attempt to get the existing value from cache.
            // If no cache entry exists, create and insert a new one using the creator function.
            if (!internCache.TryGetValue(key, out var cacheEntry))
            {
                internCache[key] = new WeakReference<TValue>(obj);
                return obj;
            }

            // If a cache entry did exist, determine if it still holds a valid value.
            if (!cacheEntry.TryGetTarget(out var result))
            {
                // Create new object and ensure the entry is still valid by re-inserting it into the cache.
                cacheEntry.SetTarget(obj);
                return obj;
            }

            return result;
        }

        private void InternCacheCleanupTimerCallback(object state)
        {
            foreach (var e in internCache)
            {
                if (!e.Value.TryGetTarget(out _))
                {
                    internCache.TryRemove(e.Key, out _);
                }
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            cacheCleanupTimer?.Dispose();
        }
    }
}
