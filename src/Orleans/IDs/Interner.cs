using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Orleans.Runtime;

namespace Orleans
{
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

        public static readonly TimeSpan DefaultCacheCleanupFreq = TimeSpan.FromMinutes(10);
    }

    /// <summary>
    /// Provide a weakly-referenced cache of interned objects.
    /// Interner is used to optimise garbage collection.
    /// We use it to store objects that are allocated frequently and may have long timelife. 
    /// This means those object may quickly fill gen 2 and cause frequent costly full heap collections.
    /// Specificaly, a message that arrives to a silo and all the headers and ids inside it may stay alive long enough to reach gen 2.
    /// Therefore, we store all ids in interner to re-use their memory accros different messages.
    /// </summary>
    /// <typeparam name="K">Type of objects to be used for intern keys</typeparam>
    /// <typeparam name="T">Type of objects to be interned / cached</typeparam>
    internal class Interner<K, T> where T : class
    {
        private static readonly string internCacheName = "Interner-" + typeof(T).Name;
        private readonly Logger logger;
        private readonly TimeSpan cacheCleanupInterval;
        private readonly SafeTimer cacheCleanupTimer;

        [NonSerialized]
        private readonly ConcurrentDictionary<K, WeakReference> internCache;

        public Interner()
            : this(InternerConstants.SIZE_SMALL)
        {
        }
        public Interner(int initialSize)
            : this(initialSize, Constants.INFINITE_TIMESPAN)
        {
        }
        public Interner(int initialSize, TimeSpan cleanupFreq)
        {
            if (initialSize <= 0) initialSize = InternerConstants.SIZE_MEDIUM;
            int concurrencyLevel = Environment.ProcessorCount * 4; // Default from ConcurrentDictionary class in .NET 4.0

            logger = LogManager.GetLogger(internCacheName, LoggerType.Runtime);

            this.internCache = new ConcurrentDictionary<K, WeakReference>(concurrencyLevel, initialSize);

            this.cacheCleanupInterval = (cleanupFreq <= TimeSpan.Zero) ? Constants.INFINITE_TIMESPAN : cleanupFreq;
            if (Constants.INFINITE_TIMESPAN != cacheCleanupInterval)
            {
                if (logger.IsVerbose) logger.Verbose(ErrorCode.Runtime_Error_100298, "Starting {0} cache cleanup timer with frequency {1}", internCacheName, cacheCleanupInterval);
                cacheCleanupTimer = new SafeTimer(InternCacheCleanupTimerCallback, null, cacheCleanupInterval, cacheCleanupInterval);
            }
#if DEBUG_INTERNER
            StringValueStatistic.FindOrCreate(internCacheName, () => String.Format("Size={0}, Content=" + Environment.NewLine + "{1}", internCache.Count, PrintInternerContent()));
#endif
        }

        /// <summary>
        /// Find cached copy of object with specified key, otherwise create new one using the supplied creator-function.
        /// </summary>
        /// <param name="key">key to find</param>
        /// <param name="creatorFunc">function to create new object and store for this key if no cached copy exists</param>
        /// <returns>Object with specified key - either previous cached copy or newly created</returns>
        public T FindOrCreate(K key, Func<T> creatorFunc)
        {
            T obj = null;
            WeakReference cacheEntry = internCache.GetOrAdd(key, 
                (k) => {
                    obj = creatorFunc();
                    return new WeakReference(obj);
                });
            if (cacheEntry != null)
            {
                if (cacheEntry.IsAlive)
                {
                    // Re-use cached object
                    obj = cacheEntry.Target as T;
                }
            }
            if (obj == null)
            {
                // Create new object
                obj = creatorFunc();
                cacheEntry = new WeakReference(obj);
                obj = internCache.AddOrUpdate(key, cacheEntry, (k, w) => cacheEntry).Target as T;
            }
            return obj;
        }

        /// <summary>
        /// Find cached copy of object with specified key, otherwise create new one using the supplied creator-function.
        /// </summary>
        /// <param name="key">key to find</param>
        /// <param name="obj">The existing value if the key is found</param>
        public bool TryFind(K key, out T obj)
        {
            obj = null;
            WeakReference cacheEntry;
            if(internCache.TryGetValue(key, out cacheEntry))
            {
                if (cacheEntry != null)
                {
                    if (cacheEntry.IsAlive)
                    {
                        obj = cacheEntry.Target as T;
                        return obj != null;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Find cached copy of object with specified key, otherwise store the supplied one. 
        /// </summary>
        /// <param name="key">key to find</param>
        /// <param name="obj">The new object to store for this key if no cached copy exists</param>
        /// <returns>Object with specified key - either previous cached copy or justed passed in</returns>
        public T Intern(K key, T obj)
        {
            return FindOrCreate(key, () => obj);
        }

        /// <summary>
        /// Intern the specified object, replacing any previous cached copy of object with specified key if the new object has a more derived type than the cached object
        /// </summary>
        /// <param name="key">object key</param>
        /// <param name="obj">object to be interned</param>
        /// <returns>Interned copy of the object with specified key</returns>
        public T InternAndUpdateWithMoreDerived(K key, T obj)
        {
            T obj1 = obj;
            WeakReference cacheEntry = internCache.GetOrAdd(key, k => new WeakReference(obj1));
            if (cacheEntry != null)
            {
                if (cacheEntry.IsAlive)
                {
                    T obj2 = cacheEntry.Target as T;

                    // Decide whether the old object or the new one has the most specific / derived type
                    Type tNew = obj.GetType();
                    Type tOld = obj2.GetType();
                    if (tNew != tOld && tOld.IsAssignableFrom(tNew))
                    {
                        // Keep and use the more specific type
                        cacheEntry.Target = obj;
                        return obj;
                    }
                    else
                    {
                        // Re-use cached object
                        return obj2;
                    }
                }
                else
                {
                    cacheEntry.Target = obj;
                    return obj;
                }
            }
            else
            {
                cacheEntry = new WeakReference(obj);
                obj = internCache.AddOrUpdate(key, cacheEntry, (k, w) => cacheEntry).Target as T;
                return obj;
            }
        }

        public void StopAndClear()
        {
            internCache.Clear();
            if(cacheCleanupTimer != null)
            {
                cacheCleanupTimer.Dispose();
            }
        }

        public List<T> AllValues()
        {
            List<T> values = new List<T>();
            foreach (var e in internCache)
            {
                if (e.Value != null && e.Value.IsAlive && e.Value.Target != null)
                {
                    T obj = e.Value.Target as T;
                    if (obj != null)
                    {
                        values.Add(obj);
                    }
                }
            }
            return values;
        }

        private void InternCacheCleanupTimerCallback(object state)
        {
            Stopwatch clock = null;
            long numEntries = 0;
            var removalResultsLoggingNeeded = logger.IsVerbose || logger.IsVerbose2;
            if (removalResultsLoggingNeeded)
            {
                clock = new Stopwatch();
                clock.Start();
                numEntries = internCache.Count;
            }

            foreach (var e in internCache)
            {
                if (e.Value == null || e.Value.IsAlive == false || e.Value.Target == null)
                {
                    WeakReference weak;
                    bool ok = internCache.TryRemove(e.Key, out weak);
                    if (!ok)
                    {
                        if (logger.IsVerbose) logger.Verbose(ErrorCode.Runtime_Error_100295, "Could not remove old {0} entry: {1} ", internCacheName, e.Key);
                    }
                }
            }

            if (!removalResultsLoggingNeeded) return;

            var numRemoved = numEntries - internCache.Count;
            if (numRemoved > 0)
            {
                if (logger.IsVerbose) logger.Verbose(ErrorCode.Runtime_Error_100296, "Removed {0} / {1} unused {2} entries in {3}", numRemoved, numEntries, internCacheName, clock.Elapsed);
            }
            else
            {
                if (logger.IsVerbose2) logger.Verbose2(ErrorCode.Runtime_Error_100296, "Removed {0} / {1} unused {2} entries in {3}", numRemoved, numEntries, internCacheName, clock.Elapsed);
            }
        }

        private string PrintInternerContent()
        {
            StringBuilder s = new StringBuilder();
          
            foreach (var e in internCache)
            {
                if (e.Value != null && e.Value.IsAlive && e.Value.Target != null)
                {
                    s.AppendLine(String.Format("{0}->{1}", e.Key, e.Value.Target));
                }
            }
            return s.ToString();
        }
    }
}
