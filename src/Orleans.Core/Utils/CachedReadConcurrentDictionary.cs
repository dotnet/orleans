using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Orleans.Utilities
{
    /// <summary>
    /// A thread-safe dictionary for read-heavy workloads.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    internal class CachedReadConcurrentDictionary<TKey, TValue> : IDictionary<TKey, TValue>
    {
        /// <summary>
        /// The number of cache misses which are tolerated before the cache is regenerated.
        /// </summary>
        private const int CacheMissesBeforeCaching = 10;
        private readonly ConcurrentDictionary<TKey, TValue> dictionary;
        private readonly IEqualityComparer<TKey> comparer;

        /// <summary>
        /// Approximate number of reads which did not hit the cache since it was last invalidated.
        /// This is used as a heuristic that the dictionary is not being modified frequently with respect to the read volume.
        /// </summary>
        private int cacheMissReads;

        /// <summary>
        /// Cached version of <see cref="dictionary"/>.
        /// </summary>
        private Dictionary<TKey, TValue> readCache;

        /// <summary>
        /// Initializes a new instance of the <see cref="CachedReadConcurrentDictionary{TKey,TValue}"/> class.
        /// </summary>
        public CachedReadConcurrentDictionary()
        {
            this.dictionary = new ConcurrentDictionary<TKey, TValue>();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CachedReadConcurrentDictionary{TKey,TValue}"/> class
        /// that contains elements copied from the specified collection.
        /// </summary>
        /// <param name="collection">
        /// The <see cref="T:IEnumerable{KeyValuePair{TKey,TValue}}"/> whose elements are copied to the new instance.
        /// </param>
        public CachedReadConcurrentDictionary(IEnumerable<KeyValuePair<TKey, TValue>> collection)
        {
            this.dictionary = new ConcurrentDictionary<TKey, TValue>(collection);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CachedReadConcurrentDictionary{TKey,TValue}"/> class
        /// that contains elements copied from the specified collection and uses the specified
        /// <see cref="T:System.Collections.Generic.IEqualityComparer{TKey}"/>.
        /// </summary>
        /// <param name="comparer">
        /// The <see cref="IEqualityComparer{TKey}"/> implementation to use when comparing keys.
        /// </param>
        public CachedReadConcurrentDictionary(IEqualityComparer<TKey> comparer)
        {
            this.comparer = comparer;
            this.dictionary = new ConcurrentDictionary<TKey, TValue>(comparer);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CachedReadConcurrentDictionary{TKey,TValue}"/>
        /// class that contains elements copied from the specified collection and uses the specified
        /// <see cref="T:System.Collections.Generic.IEqualityComparer{TKey}"/>.
        /// </summary>
        /// <param name="collection">
        /// The <see cref="T:IEnumerable{KeyValuePair{TKey,TValue}}"/> whose elements are copied to the new instance.
        /// </param>
        /// <param name="comparer">
        /// The <see cref="IEqualityComparer{TKey}"/> implementation to use when comparing keys.
        /// </param>
        public CachedReadConcurrentDictionary(IEnumerable<KeyValuePair<TKey, TValue>> collection, IEqualityComparer<TKey> comparer)
        {
            this.comparer = comparer;
            this.dictionary = new ConcurrentDictionary<TKey, TValue>(collection, comparer);
        }

        /// <inheritdoc />
        IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

        /// <inheritdoc />
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => this.GetReadDictionary().GetEnumerator();

        /// <inheritdoc />
        public void Add(KeyValuePair<TKey, TValue> item)
        {
            ((IDictionary<TKey, TValue>) this.dictionary).Add(item);
            this.InvalidateCache();
        }

        /// <inheritdoc />
        public void Clear()
        {
            this.dictionary.Clear();
            this.InvalidateCache();
        }

        /// <inheritdoc />
        public bool Contains(KeyValuePair<TKey, TValue> item) => this.GetReadDictionary().Contains(item);

        /// <inheritdoc />
        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            this.GetReadDictionary().CopyTo(array, arrayIndex);
        }

        /// <inheritdoc />
        public bool Remove(KeyValuePair<TKey, TValue> item)
        {
            var result = ((IDictionary<TKey, TValue>) this.dictionary).Remove(item);
            if (result) this.InvalidateCache();
            return result;
        }

        /// <inheritdoc />
        public int Count => this.GetReadDictionary().Count;

        /// <inheritdoc />
        public bool IsReadOnly => false;

        /// <inheritdoc />
        public void Add(TKey key, TValue value)
        {
            ((IDictionary<TKey, TValue>) this.dictionary).Add(key, value);
            this.InvalidateCache();
        }

        /// <summary>
        /// dds a key/value pair to the <see cref="CachedReadConcurrentDictionary{TKey,TValue}"/> if the key does not exist.
        /// </summary>
        /// <param name="key">The key of the element to add.</param>
        /// <param name="valueFactory">The function used to generate a value for the key</param>
        /// <returns>The value for the key. This will be either the existing value for the key if the key is already in the dictionary, or the new value if the key was not in the dictionary.</returns>
        public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
        {
            TValue value;

            if (this.GetReadDictionary().TryGetValue(key, out value))
                return value;

            value = this.dictionary.GetOrAdd(key, valueFactory);
            InvalidateCache();

            return value;
        }

        /// <summary>
        /// Attempts to add the specified key and value.
        /// </summary>
        /// <param name="key">The key of the element to add.</param>
        /// <param name="value">The value of the element to add. The value can be a null reference (Nothing
        /// in Visual Basic) for reference types.</param>
        /// <returns>true if the key/value pair was added successfully; otherwise, false.</returns>
        public bool TryAdd(TKey key, TValue value)
        {
            if (this.dictionary.TryAdd(key, value))
            {
                this.InvalidateCache();
                return true;
            }

            return false;
        }

        /// <inheritdoc />
        public bool ContainsKey(TKey key) => this.GetReadDictionary().ContainsKey(key);

        /// <inheritdoc />
        public bool Remove(TKey key)
        {
            var result = ((IDictionary<TKey, TValue>) this.dictionary).Remove(key);
            if (result) this.InvalidateCache();
            return result;
        }

        /// <inheritdoc />
        public bool TryGetValue(TKey key, out TValue value) => this.GetReadDictionary().TryGetValue(key, out value);

        /// <inheritdoc />
        public TValue this[TKey key]
        {
            get { return this.GetReadDictionary()[key]; }
            set
            {
                this.dictionary[key] = value;
                this.InvalidateCache();
            }
        }

        /// <inheritdoc />
        public ICollection<TKey> Keys => this.GetReadDictionary().Keys;

        /// <inheritdoc />
        public ICollection<TValue> Values => this.GetReadDictionary().Values;
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private IDictionary<TKey, TValue> GetReadDictionary() => this.readCache ?? this.GetWithoutCache();

        private IDictionary<TKey, TValue> GetWithoutCache()
        {
            // If the dictionary was recently modified or the cache is being recomputed, return the dictionary directly.
            if (Interlocked.Increment(ref this.cacheMissReads) < CacheMissesBeforeCaching) return this.dictionary;

            // Recompute the cache if too many cache misses have occurred.
            this.cacheMissReads = 0;
            return this.readCache = new Dictionary<TKey, TValue>(this.dictionary, this.comparer);
        }

        private void InvalidateCache()
        {
            this.cacheMissReads = 0;
            this.readCache = null;
        }
    }
}