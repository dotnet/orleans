#nullable enable
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Orleans.Caching.Internal;

namespace Orleans.Caching;

/// <summary>
/// A pseudo LRU based on the TU-Q eviction policy. The LRU list is composed of 3 segments: hot, warm and cold.
/// Cost of maintaining segments is amortized across requests. Items are only cycled when capacity is exceeded.
/// Pure read does not cycle items if all segments are within capacity constraints. There are no global locks.
/// On cache miss, a new item is added. Tail items in each segment are dequeued, examined, and are either enqueued
/// or discarded.
/// The TU-Q scheme of hot, warm and cold is similar to that used in MemCached (https://memcached.org/blog/modern-lru/)
/// and OpenBSD (https://flak.tedunangst.com/post/2Q-buffer-cache-algorithm), but does not use a background thread
/// to maintain the internal queues.
/// </summary>
/// <remarks>
/// This implementation is derived from BitFaster.Caching (https://github.com/bitfaster/BitFaster.Caching), removing
/// functionality that is not needed for Orleans (async, custom policies), to reduce the number of source files.
/// 
/// Each segment has a capacity. When segment capacity is exceeded, items are moved as follows:
/// <list type="number">
///   <item><description>New items are added to hot, WasAccessed = false.</description></item>
///   <item><description>When items are accessed, update WasAccessed = true.</description></item>
///   <item><description>When items are moved WasAccessed is set to false.</description></item>
///   <item><description>When hot is full, hot tail is moved to either Warm or Cold depending on WasAccessed.</description></item>
///   <item><description>When warm is full, warm tail is moved to warm head or cold depending on WasAccessed.</description></item>
///   <item><description>When cold is full, cold tail is moved to warm head or removed from dictionary on depending on WasAccessed.</description></item>
///</list>
/// </remarks>
/// <remarks>
/// Initializes a new instance of the ConcurrentLruCore class with the specified concurrencyLevel, capacity, equality comparer, item policy and telemetry policy.
/// </remarks>
/// <param name="capacity">The capacity.</param>
/// <param name="comparer">The equality comparer.</param>
internal class ConcurrentLruCache<K, V>(
    int capacity,
    IEqualityComparer<K>? comparer) : IEnumerable<KeyValuePair<K, V>>, ICacheMetrics, ConcurrentLruCache<K, V>.ITestAccessor
    where K : notnull
{
    private readonly ConcurrentDictionary<K, LruItem> _dictionary = new(concurrencyLevel: -1, capacity: capacity, comparer: comparer);
    private readonly ConcurrentQueue<LruItem> _hotQueue = new();
    private readonly ConcurrentQueue<LruItem> _warmQueue = new();
    private readonly ConcurrentQueue<LruItem> _coldQueue = new();
    private readonly CapacityPartition _capacity = new(capacity);
    private readonly TelemetryPolicy _telemetryPolicy = new();

    // maintain count outside ConcurrentQueue, since ConcurrentQueue.Count holds a global lock
    private PaddedQueueCount _counter;
    private bool _isWarm;

    /// <summary>
    /// Initializes a new instance of the ConcurrentLruCore class with the specified capacity.
    /// </summary>
    /// <param name="capacity">The capacity.</param>
    public ConcurrentLruCache(int capacity) : this(capacity, comparer: null)
    {
    }

    // No lock count: https://arbel.net/2013/02/03/best-practices-for-using-concurrentdictionary/
    ///<inheritdoc/>
    public int Count => _dictionary.Where(_ => true).Count();

    ///<inheritdoc/>
    public int Capacity => _capacity.Hot + _capacity.Warm + _capacity.Cold;

    ///<inheritdoc/>
    public ICacheMetrics Metrics => this;

    /// <summary>
    /// Gets the number of hot items.
    /// </summary>
    public int HotCount => Volatile.Read(ref _counter.Hot);

    /// <summary>
    /// Gets the number of warm items.
    /// </summary>
    public int WarmCount => Volatile.Read(ref _counter.Warm);

    /// <summary>
    /// Gets the number of cold items.
    /// </summary>
    public int ColdCount => Volatile.Read(ref _counter.Cold);

    /// <summary>
    /// Gets a collection containing the keys in the cache.
    /// </summary>
    public ICollection<K> Keys => _dictionary.Keys;

    /// <summary>Returns an enumerator that iterates through the cache.</summary>
    /// <returns>An enumerator for the cache.</returns>
    /// <remarks>
    /// The enumerator returned from the cache is safe to use concurrently with
    /// reads and writes, however it does not represent a moment-in-time snapshot.
    /// The contents exposed through the enumerator may contain modifications
    /// made after <see cref="GetEnumerator"/> was called.
    /// </remarks>
    public IEnumerator<KeyValuePair<K, V>> GetEnumerator()
    {
        foreach (var kvp in _dictionary)
        {
            yield return new KeyValuePair<K, V>(kvp.Key, kvp.Value.Value);
        }
    }

    ///<inheritdoc/>
    public V Get(K key)
    {
        if (!TryGet(key, out var value))
        {
            throw new KeyNotFoundException($"Key '{key}' not found in the cache.");
        }

        return value;
    }

    ///<inheritdoc/>
    public bool TryGet(K key, [MaybeNullWhen(false)] out V value)
    {
        if (_dictionary.TryGetValue(key, out var item))
        {
            value = item.Value;
            item.MarkAccessed();
            _telemetryPolicy.IncrementHit();
            return true;
        }

        value = default;
        _telemetryPolicy.IncrementMiss();
        return false;
    }

    public bool TryAdd(K key, V value)
    {
        var newItem = new LruItem(key, value);

        if (_dictionary.TryAdd(key, newItem))
        {
            _hotQueue.Enqueue(newItem);
            Cycle(Interlocked.Increment(ref _counter.Hot));
            return true;
        }

        DisposeValue(newItem.Value);

        return false;
    }

    ///<inheritdoc/>
    public V GetOrAdd(K key, Func<K, V> valueFactory)
    {
        while (true)
        {
            if (TryGet(key, out var value))
            {
                return value;
            }

            // The value factory may be called concurrently for the same key, but the first write to the dictionary wins.
            value = valueFactory(key);

            if (TryAdd(key, value))
            {
                return value;
            }
        }
    }

    /// <summary>
    /// Adds a key/value pair to the cache if the key does not already exist. Returns the new value, or the
    /// existing value if the key already exists.
    /// </summary>
    /// <typeparam name="TArg">The type of an argument to pass into valueFactory.</typeparam>
    /// <param name="key">The key of the element to add.</param>
    /// <param name="valueFactory">The factory function used to generate a value for the key.</param>
    /// <param name="factoryArgument">An argument value to pass into valueFactory.</param>
    /// <returns>The value for the key. This will be either the existing value for the key if the key is already
    /// in the cache, or the new value if the key was not in the cache.</returns>
    public V GetOrAdd<TArg>(K key, Func<K, TArg, V> valueFactory, TArg factoryArgument)
    {
        while (true)
        {
            if (TryGet(key, out var value))
            {
                return value;
            }

            // The value factory may be called concurrently for the same key, but the first write to the dictionary wins.
            value = valueFactory(key, factoryArgument);

            if (TryAdd(key, value))
            {
                return value;
            }
        }
    }

    /// <summary>
    /// Attempts to remove the specified key value pair.
    /// </summary>
    /// <param name="predicate">The predicate used to determine if the item should be removed.</param>
    /// <param name="predicateArgument">Argument passed to the predicate.</param>
    /// <returns>true if the item was removed successfully; otherwise, false.</returns>
    public bool TryRemove<TArg>(K key, Func<V, TArg, bool> predicate, TArg predicateArgument)
    {
        if (_dictionary.TryGetValue(key, out var existing))
        {
            lock (existing)
            {
                if (predicate(existing.Value, predicateArgument))
                {
                    var kvp = new KeyValuePair<K, LruItem>(key, existing);
                    if (_dictionary.TryRemove(kvp))
                    {
                        OnRemove(kvp.Value, ItemRemovedReason.Removed);
                        return true;
                    }
                }
            }

            // it existed, but we couldn't remove - this means value was replaced after the TryGetValue (a race)
        }

        return false;
    }

    /// <summary>
    /// Attempts to remove the specified key value pair.
    /// </summary>
    /// <param name="item">The item to remove.</param>
    /// <returns>true if the item was removed successfully; otherwise, false.</returns>
    public bool TryRemove(KeyValuePair<K, V> item)
    {
        if (_dictionary.TryGetValue(item.Key, out var existing))
        {
            lock (existing)
            {
                if (EqualityComparer<V>.Default.Equals(existing.Value, item.Value))
                {
                    var kvp = new KeyValuePair<K, LruItem>(item.Key, existing);
                    if (_dictionary.TryRemove(kvp))
                    {
                        OnRemove(kvp.Value, ItemRemovedReason.Removed);
                        return true;
                    }
                }
            }

            // it existed, but we couldn't remove - this means value was replaced after the TryGetValue (a race)
        }

        return false;
    }

    /// <summary>
    /// Attempts to remove and return the value that has the specified key.
    /// </summary>
    /// <param name="key">The key of the element to remove.</param>
    /// <param name="value">When this method returns, contains the object removed, or the default value of the value type if key does not exist.</param>
    /// <returns>true if the object was removed successfully; otherwise, false.</returns>
    public bool TryRemove(K key, [MaybeNullWhen(false)] out V value)
    {
        if (_dictionary.TryRemove(key, out var item))
        {
            OnRemove(item, ItemRemovedReason.Removed);
            value = item.Value;
            return true;
        }

        value = default;
        return false;
    }

    ///<inheritdoc/>
    public bool TryRemove(K key) => TryRemove(key, out _);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void OnRemove(LruItem item, ItemRemovedReason reason)
    {
        // Mark as not accessed, it will later be cycled out of the queues because it can never be fetched
        // from the dictionary. Note: Hot/Warm/Cold count will reflect the removed item until it is cycled
        // from the queue.
        item.WasAccessed = false;
        item.WasRemoved = true;

        if (reason == ItemRemovedReason.Evicted)
        {
            _telemetryPolicy.IncrementEvicted();
        }

        // serialize dispose (common case dispose not thread safe)
        lock (item)
        {
            DisposeValue(item.Value);
        }
    }

    ///<inheritdoc/>
    ///<remarks>Note: Calling this method does not affect LRU order.</remarks>
    public bool TryUpdate(K key, V value)
    {
        if (_dictionary.TryGetValue(key, out var existing))
        {
            lock (existing)
            {
                if (!existing.WasRemoved)
                {
                    var oldValue = existing.Value;

                    existing.Value = value;

                    _telemetryPolicy.IncrementUpdated();
                    DisposeValue(oldValue);

                    return true;
                }
            }
        }

        return false;
    }

    ///<inheritdoc/>
    ///<remarks>Note: Updates to existing items do not affect LRU order. Added items are at the top of the LRU.</remarks>
    public void AddOrUpdate(K key, V value)
    {
        while (true)
        {
            // first, try to update
            if (TryUpdate(key, value))
            {
                return;
            }

            // then try add
            var newItem = new LruItem(key, value);

            if (_dictionary.TryAdd(key, newItem))
            {
                _hotQueue.Enqueue(newItem);
                Cycle(Interlocked.Increment(ref _counter.Hot));
                return;
            }

            // if both update and add failed there was a race, try again
        }
    }

    ///<inheritdoc/>
    public void Clear()
    {
        // don't overlap Clear/Trim/TrimExpired
        lock (_dictionary)
        {
            // evaluate queue count, remove everything including items removed from the dictionary but
            // not the queues. This also avoids the expensive o(n) no lock count, or locking the dictionary.
            var queueCount = HotCount + WarmCount + ColdCount;
            TrimLiveItems(queueCount, ItemRemovedReason.Cleared);
        }
    }

    /// <summary>
    /// Trim the specified number of items from the cache. Removes items in LRU order.
    /// </summary>
    /// <param name="itemCount">The number of items to remove.</param>
    /// <returns>The number of items removed from the cache.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="itemCount"/> is less than 0./</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="itemCount"/> is greater than capacity./</exception>
    /// <remarks>
    /// Note: Trim affects LRU order. Calling Trim resets the internal accessed status of items.
    /// </remarks>
    public void Trim(int itemCount)
    {
        var capacity = Capacity;
        ArgumentOutOfRangeException.ThrowIfLessThan(itemCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(itemCount, capacity);

        // clamp itemCount to number of items actually in the cache
        itemCount = Math.Min(itemCount, HotCount + WarmCount + ColdCount);

        // don't overlap Clear/Trim/TrimExpired
        lock (_dictionary)
        {
            TrimLiveItems(itemCount, ItemRemovedReason.Trimmed);
        }
    }

    private void TrimLiveItems(int itemCount, ItemRemovedReason reason)
    {
        // When items are touched, they are moved to warm by cycling. Therefore, to guarantee
        // that we can remove itemCount items, we must cycle (2 * capacity.Warm) + capacity.Hot times.
        // If clear is called during trimming, it would be possible to get stuck in an infinite
        // loop here. The warm + hot limit also guards against this case.
        var trimWarmAttempts = 0;
        var itemsRemoved = 0;
        var maxWarmHotAttempts = _capacity.Warm * 2 + _capacity.Hot;

        while (itemsRemoved < itemCount && trimWarmAttempts < maxWarmHotAttempts)
        {
            if (Volatile.Read(ref _counter.Cold) > 0)
            {
                if (TryRemoveCold(reason) == (ItemDestination.Remove, 0))
                {
                    itemsRemoved++;
                    trimWarmAttempts = 0;
                }
                else
                {
                    TrimWarmOrHot(reason);
                }
            }
            else
            {
                TrimWarmOrHot(reason);
                trimWarmAttempts++;
            }
        }

        if (Volatile.Read(ref _counter.Warm) < _capacity.Warm)
        {
            Volatile.Write(ref _isWarm, false);
        }
    }

    private void TrimWarmOrHot(ItemRemovedReason reason)
    {
        if (Volatile.Read(ref _counter.Warm) > 0)
        {
            CycleWarmUnchecked(reason);
        }
        else if (Volatile.Read(ref _counter.Hot) > 0)
        {
            CycleHotUnchecked(reason);
        }
    }

    private void Cycle(int hotCount)
    {
        if (_isWarm)
        {
            (var dest, var count) = CycleHot(hotCount);

            var cycles = 0;
            while (cycles++ < 3 && dest != ItemDestination.Remove)
            {
                if (dest == ItemDestination.Warm)
                {
                    (dest, count) = CycleWarm(count);
                }
                else if (dest == ItemDestination.Cold)
                {
                    (dest, count) = CycleCold(count);
                }
            }

            // If nothing was removed yet, constrain the size of warm and cold by discarding the coldest item.
            if (dest != ItemDestination.Remove)
            {
                if (dest == ItemDestination.Warm && count > _capacity.Warm)
                {
                    count = LastWarmToCold();
                }

                ConstrainCold(count, ItemRemovedReason.Evicted);
            }
        }
        else
        {
            // fill up the warm queue with new items until warm is full.
            // else during warmup the cache will only use the hot + cold queues until any item is requested twice.
            CycleDuringWarmup(hotCount);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CycleDuringWarmup(int hotCount)
    {
        // do nothing until hot is full
        if (hotCount > _capacity.Hot)
        {
            Interlocked.Decrement(ref _counter.Hot);

            if (_hotQueue.TryDequeue(out var item))
            {
                // special case: removed during warmup
                if (item.WasRemoved)
                {
                    return;
                }

                var count = Move(item, ItemDestination.Warm, ItemRemovedReason.Evicted);

                // if warm is now full, overflow to cold and mark as warm
                if (count > _capacity.Warm)
                {
                    Volatile.Write(ref _isWarm, true);
                    count = LastWarmToCold();
                    ConstrainCold(count, ItemRemovedReason.Evicted);
                }
            }
            else
            {
                Interlocked.Increment(ref _counter.Hot);
            }
        }
    }

    private (ItemDestination, int) CycleHot(int hotCount)
    {
        if (hotCount > _capacity.Hot)
        {
            return CycleHotUnchecked(ItemRemovedReason.Evicted);
        }

        return (ItemDestination.Remove, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (ItemDestination, int) CycleHotUnchecked(ItemRemovedReason removedReason)
    {
        Interlocked.Decrement(ref _counter.Hot);

        if (_hotQueue.TryDequeue(out var item))
        {
            var where = RouteHot(item);
            return (where, Move(item, where, removedReason));
        }
        else
        {
            Interlocked.Increment(ref _counter.Hot);
            return (ItemDestination.Remove, 0);
        }
    }

    private (ItemDestination, int) CycleWarm(int count)
    {
        if (count > _capacity.Warm)
        {
            return CycleWarmUnchecked(ItemRemovedReason.Evicted);
        }

        return (ItemDestination.Remove, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (ItemDestination, int) CycleWarmUnchecked(ItemRemovedReason removedReason)
    {
        var wc = Interlocked.Decrement(ref _counter.Warm);

        if (_warmQueue.TryDequeue(out var item))
        {
            if (item.WasRemoved)
            {
                return (ItemDestination.Remove, 0);
            }

            var where = RouteWarm(item);

            // When the warm queue is full, we allow an overflow of 1 item before redirecting warm items to cold.
            // This only happens when hit rate is high, in which case we can consider all items relatively equal in
            // terms of which was least recently used.
            if (where == ItemDestination.Warm && wc <= _capacity.Warm)
            {
                return (ItemDestination.Warm, Move(item, where, removedReason));
            }
            else
            {
                return (ItemDestination.Cold, Move(item, ItemDestination.Cold, removedReason));
            }
        }
        else
        {
            Interlocked.Increment(ref _counter.Warm);
            return (ItemDestination.Remove, 0);
        }
    }

    private (ItemDestination, int) CycleCold(int count)
    {
        if (count > _capacity.Cold)
        {
            return TryRemoveCold(ItemRemovedReason.Evicted);
        }

        return (ItemDestination.Remove, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (ItemDestination, int) TryRemoveCold(ItemRemovedReason removedReason)
    {
        Interlocked.Decrement(ref _counter.Cold);

        if (_coldQueue.TryDequeue(out var item))
        {
            var where = RouteCold(item);
            if (where == ItemDestination.Warm && Volatile.Read(ref _counter.Warm) <= _capacity.Warm)
            {
                return (ItemDestination.Warm, Move(item, where, removedReason));
            }
            else
            {
                Move(item, ItemDestination.Remove, removedReason);
                return (ItemDestination.Remove, 0);
            }
        }
        else
        {
            return (ItemDestination.Cold, Interlocked.Increment(ref _counter.Cold));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int LastWarmToCold()
    {
        Interlocked.Decrement(ref _counter.Warm);

        if (_warmQueue.TryDequeue(out var item))
        {
            var destination = item.WasRemoved ? ItemDestination.Remove : ItemDestination.Cold;
            return Move(item, destination, ItemRemovedReason.Evicted);
        }
        else
        {
            Interlocked.Increment(ref _counter.Warm);
            return 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ConstrainCold(int coldCount, ItemRemovedReason removedReason)
    {
        if (coldCount > _capacity.Cold && _coldQueue.TryDequeue(out var item))
        {
            Interlocked.Decrement(ref _counter.Cold);
            Move(item, ItemDestination.Remove, removedReason);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Move(LruItem item, ItemDestination where, ItemRemovedReason removedReason)
    {
        item.WasAccessed = false;

        switch (where)
        {
            case ItemDestination.Warm:
                _warmQueue.Enqueue(item);
                return Interlocked.Increment(ref _counter.Warm);
            case ItemDestination.Cold:
                _coldQueue.Enqueue(item);
                return Interlocked.Increment(ref _counter.Cold);
            case ItemDestination.Remove:

                var kvp = new KeyValuePair<K, LruItem>(item.Key, item);

                if (_dictionary.TryRemove(kvp))
                {
                    OnRemove(item, removedReason);
                }

                break;
        }

        return 0;
    }

    /// <summary>Returns an enumerator that iterates through the cache.</summary>
    /// <returns>An enumerator for the cache.</returns>
    /// <remarks>
    /// The enumerator returned from the cache is safe to use concurrently with
    /// reads and writes, however it does not represent a moment-in-time snapshot.
    /// The contents exposed through the enumerator may contain modifications
    /// made after <see cref="GetEnumerator"/> was called.
    /// </remarks>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

#if DEBUG
    /// <summary>
    /// Format the LRU as a string by converting all the keys to strings.
    /// </summary>
    /// <returns>The LRU formatted as a string.</returns>
    internal string FormatLruString()
    {
        var sb = new System.Text.StringBuilder();

        sb.Append("Hot [");
        sb.Append(string.Join(",", _hotQueue.Select(n => n.Key.ToString())));
        sb.Append("] Warm [");
        sb.Append(string.Join(",", _warmQueue.Select(n => n.Key.ToString())));
        sb.Append("] Cold [");
        sb.Append(string.Join(",", _coldQueue.Select(n => n.Key.ToString())));
        sb.Append(']');

        return sb.ToString();
    }
#endif

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ItemDestination RouteHot(LruItem item)
    {
        if (item.WasRemoved)
        {
            return ItemDestination.Remove;
        }

        if (item.WasAccessed)
        {
            return ItemDestination.Warm;
        }

        return ItemDestination.Cold;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ItemDestination RouteWarm(LruItem item)
    {
        if (item.WasRemoved)
        {
            return ItemDestination.Remove;
        }

        if (item.WasAccessed)
        {
            return ItemDestination.Warm;
        }

        return ItemDestination.Cold;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ItemDestination RouteCold(LruItem item)
    {
        if (item.WasAccessed & !item.WasRemoved)
        {
            return ItemDestination.Warm;
        }

        return ItemDestination.Remove;
    }

    double ICacheMetrics.HitRatio => _telemetryPolicy.HitRatio;

    long ICacheMetrics.Total => _telemetryPolicy.Total;

    long ICacheMetrics.Hits => _telemetryPolicy.Hits;

    long ICacheMetrics.Misses => _telemetryPolicy.Misses;

    long ICacheMetrics.Evicted => _telemetryPolicy.Evicted;

    long ICacheMetrics.Updated => _telemetryPolicy.Updated;

    ConcurrentQueue<LruItem> ITestAccessor.HotQueue => _hotQueue;
    ConcurrentQueue<LruItem> ITestAccessor.WarmQueue => _warmQueue;
    ConcurrentQueue<LruItem> ITestAccessor.ColdQueue => _coldQueue;
    ConcurrentDictionary<K, LruItem> ITestAccessor.Dictionary => _dictionary;
    bool ITestAccessor.IsWarm => _isWarm;

    /// <summary>
    /// Represents an LRU item.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the LruItem class with the specified key and value.
    /// </remarks>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    // NOTE: Internal for testing
    [DebuggerDisplay("[{Key}] = {Value}")]
    internal sealed class LruItem(K key, V value)
    {
        private V _data = value;

        // only used when V is a non-atomic value type to prevent torn reads
        private int _sequence;

        /// <summary>
        /// Gets the key.
        /// </summary>
        public readonly K Key = key;

        /// <summary>
        /// Gets or sets the value.
        /// </summary>
        public V Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (TypeProps<V>.IsWriteAtomic)
                {
                    return _data;
                }
                else
                {
                    return SeqLockRead();
                }
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                if (TypeProps<V>.IsWriteAtomic)
                {
                    _data = value;
                }
                else
                {
                    SeqLockWrite(value);
                }
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether the item was accessed.
        /// </summary>
        public bool WasAccessed { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the item was removed.
        /// </summary>
        public bool WasRemoved { get; set; }

        /// <summary>
        /// Marks the item as accessed, if it was not already accessed.
        /// </summary>
        public void MarkAccessed()
        {
            if (!WasAccessed)
            {
                WasAccessed = true;
            }
        }

        internal V SeqLockRead()
        {
            var spin = new SpinWait();
            while (true)
            {
                var start = Volatile.Read(ref _sequence);

                if ((start & 1) == 1)
                {
                    // A write is in progress, spin.
                    spin.SpinOnce();
                    continue;
                }

                var copy = _data;

                var end = Volatile.Read(ref _sequence);
                if (start == end)
                {
                    return copy;
                }
            }
        }

        // Note: LruItem should be locked while invoking this method. Multiple writer threads are not supported.
        internal void SeqLockWrite(V value)
        {
            Interlocked.Increment(ref _sequence);

            _data = value;

            Interlocked.Increment(ref _sequence);
        }
    }

    /// <summary>
    /// Represents a telemetry policy with counters and events.
    /// </summary>
    [DebuggerDisplay("Hit = {Hits}, Miss = {Misses}, Upd = {Updated}, Evict = {Evicted}")]
    internal readonly struct TelemetryPolicy
    {
        private readonly Counter _hitCount = new();
        private readonly Counter _missCount = new();
        private readonly Counter _evictedCount = new();
        private readonly Counter _updatedCount = new();

        public TelemetryPolicy()
        {
        }

        ///<inheritdoc/>
        public readonly double HitRatio => Total == 0 ? 0 : Hits / (double)Total;

        ///<inheritdoc/>
        public readonly long Total => _hitCount.Count() + _missCount.Count();

        ///<inheritdoc/>
        public readonly long Hits => _hitCount.Count();

        ///<inheritdoc/>
        public readonly long Misses => _missCount.Count();

        ///<inheritdoc/>
        public readonly long Evicted => _evictedCount.Count();

        ///<inheritdoc/>
        public readonly long Updated => _updatedCount.Count();

        ///<inheritdoc/>
        public readonly void IncrementMiss() => _missCount.Increment();

        ///<inheritdoc/>
        public readonly void IncrementHit() => _hitCount.Increment();

        ///<inheritdoc/>
        public readonly void IncrementEvicted() => _evictedCount.Increment();

        ///<inheritdoc/>
        public readonly void IncrementUpdated() => _updatedCount.Increment();
    }

    private enum ItemDestination
    {
        Warm,
        Cold,
        Remove
    }

    private enum ItemRemovedReason
    {
        Removed,
        Evicted,
        Cleared,
        Trimmed,
    }

    internal interface ITestAccessor
    {
        public ConcurrentQueue<LruItem> HotQueue { get; }
        public ConcurrentQueue<LruItem> WarmQueue { get; }
        public ConcurrentQueue<LruItem> ColdQueue { get; }
        public ConcurrentDictionary<K, LruItem> Dictionary { get; }
        public bool IsWarm { get; }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DisposeValue(V value)
    {
        if (value is IDisposable d)
        {
            d.Dispose();
        }
    }
}
