using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;


namespace Orleans.Runtime
{
    /// <summary>
    /// This class implements an LRU (Least-Recently Used) cache of grain references. It keeps a bounded set of values and will age-out "old" values 
    /// </summary>
    /// <typeparam name="TKey"></typeparam>
    /// <typeparam name="TValue"></typeparam>
    internal class GrainReferenceCache<TKey, TValue> : IDisposable
        where TValue : IAddressable
    {
        // Delegate type for fetching the value associated with a given key.
        public delegate TValue FetchValueDelegate(TKey key);
        // Delegate type for casting IAddressable to TValue
        public delegate TValue CastDelegate(IAddressable reference);

        private long nextGeneration = 0;
        private long generationToFree = 0;
        private readonly int maximumCount;
        private readonly TimeSpan requiredFreshness;

        private class TimestampedValue
        {
            public DateTime WhenLoaded;
            public TValue Value;
            public long Generation;
        }

        private readonly Dictionary<TKey, TimestampedValue> cache;

        private readonly FetchValueDelegate fetcher;
        private readonly ReaderWriterLockSlim rwLock;

        /// <summary>
        /// Creates a new LRU (Least-Recently Used) cache of GrainReferences.
        /// </summary>
        /// <param name="maxSize">Maximum number of entries to allow.</param>
        /// <param name="maxAge">Maximum age of an entry.</param>
        /// <param name="f"> Delegate for fetching the value associated with a given key</param>
        /// <param name="c"> Delegate for casting IAddressable to TValue</param>
        public GrainReferenceCache(int maxSize, TimeSpan maxAge, FetchValueDelegate f, CastDelegate c)
        {
            maximumCount = maxSize;
            requiredFreshness = maxAge;
            fetcher = f;
            cache = new Dictionary<TKey, TimestampedValue>();
            rwLock = new ReaderWriterLockSlim();
        }

        /// <summary>
        /// Return the number of entries currently in the cache
        /// </summary>
        public int Count
        {
            get
            {
                try
                {
                    rwLock.EnterReadLock();

                    return cache.Count;
                }
                finally
                {
                    rwLock.ExitReadLock();
                }
            }
        }

        /// <summary>
        /// Get a grain reference for the specified cache-key.
        /// The grain reference will either be taken from cahce, or a new one will be created by calling the <c>FetchValueDelegate</c>
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public TValue Get(TKey key)
        {
            TimestampedValue result;
            bool readerLockUpgraded = false;

            try
            {
                rwLock.EnterReadLock();

                if (cache.TryGetValue(key, out result))
                {
                    result.Generation = Interlocked.Increment(ref nextGeneration);
                    TimeSpan age = result.WhenLoaded.Subtract(DateTime.UtcNow);
                    if (age > requiredFreshness)
                    {
                        try
                        {
                            rwLock.ExitReadLock();
                            readerLockUpgraded = true;
                            rwLock.EnterWriteLock();
                            cache.Remove(key);
                        }
                        finally
                        {
                            rwLock.ExitWriteLock();
                        }
                        result = null;
                    }
                }

                if (result != null)
                    return result.Value;
            }
            finally
            {
                if (!readerLockUpgraded)
                    rwLock.ExitReadLock();
            }

            try
            {
                rwLock.EnterWriteLock();

                if (cache.TryGetValue(key, out result))
                {
                    result.Generation = Interlocked.Increment(ref nextGeneration);
                    return result.Value;
                }

                while (cache.Count >= maximumCount)
                {
                    long generationToDelete = Interlocked.Increment(ref generationToFree);
                    KeyValuePair<TKey, TimestampedValue> entryToFree =
                        cache.FirstOrDefault(kvp => kvp.Value.Generation == generationToDelete);

                    if (entryToFree.Key != null)
                    {
                        cache.Remove(entryToFree.Key);
                    }
                }

                result = new TimestampedValue { Generation = Interlocked.Increment(ref nextGeneration) };
                try
                {
                    var r = fetcher(key);
                    result.Value = r;
                    result.WhenLoaded = DateTime.UtcNow;
                    cache.Add(key, result);
                }
                catch (Exception)
                {
                    if (cache.ContainsKey(key))
                        cache.Remove(key);
                    throw;
                }
            }
            finally
            {
                rwLock.ExitWriteLock();
            }
            return result.Value;
        }

        public void Dispose()
        {
            rwLock.Dispose();
        }
    }
}
