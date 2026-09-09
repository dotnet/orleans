// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Adapted from dotnet/runtime ConcurrentDictionary.cs at b5881242dc356bd8c464026387545f2684079d30.
// See ConcurrentHashRangeDictionary.md for provenance and the adaptation/maintenance contract.

using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using Orleans.Runtime.GrainDirectory;

namespace Orleans.Runtime.Utilities;

/// <summary>
/// A concurrent hash table whose buckets cover contiguous portions of a stable 32-bit hash ring.
/// Point and range operations access the same nodes and bucket array.
/// </summary>
/// <remarks>
/// The equality comparer defines both key equality and the table's hash coordinate.
/// Hash coordinates are the comparer's unsigned 32-bit hash codes.
/// Keys retain their hash and equality identity while stored. The comparer supports concurrent calls.
/// Point operations use lock-free reads and striped writes. Enumeration captures one table generation and
/// observes concurrent changes to that table, with the same consistency model as ConcurrentDictionary.
/// Callers requiring a point-in-time snapshot quiesce mutations while materializing the enumeration.
/// </remarks>
internal sealed class ConcurrentHashRangeDictionary<TKey, TValue, TComparer> : IEnumerable<KeyValuePair<TKey, TValue>>
    where TKey : notnull
    where TComparer : IConsistentHashComparer<TKey>
{
    private const int MaxCapacity = 1 << 30;
    private const int MaxLockCount = 1024;
    private readonly TComparer _comparer;
    private readonly bool _growLocks;
    private volatile Tables _tables;
    private int _budget;

    public ConcurrentHashRangeDictionary(
        TComparer comparer,
        int capacity = 32,
        int concurrencyLevel = -1)
    {
        ArgumentNullException.ThrowIfNull(comparer);
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, MaxCapacity);
        _growLocks = concurrencyLevel == -1;
        if (_growLocks)
        {
            concurrencyLevel = Environment.ProcessorCount;
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(concurrencyLevel, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(concurrencyLevel, MaxCapacity);
        var lockCount = (int)BitOperations.RoundUpToPowerOf2((uint)concurrencyLevel);
        // Leave room for uneven lock occupancy before the requested entry count triggers a resize.
        var initialCapacity = (int)Math.Min(MaxCapacity, (4L * capacity + 2) / 3);
        var bucketCount = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(2, Math.Max(initialCapacity, lockCount)));
        var locks = new object[lockCount];
        locks[0] = locks;
        for (var i = 1; i < locks.Length; i++)
        {
            locks[i] = new object();
        }

        _comparer = comparer;
        _tables = new Tables(new VolatileNode[bucketCount], locks, new int[lockCount]);
        _budget = Math.Max(1, bucketCount / lockCount);
    }

    public TValue this[TKey key]
    {
        get => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException();
        set => TryAddInternal(key, value, updateIfExists: true);
    }

    public TComparer Comparer => _comparer;

    /// <summary>Gets the entry count while holding all writer locks.</summary>
    public int Count
    {
        get
        {
            var locksAcquired = 0;
            try
            {
                var tables = AcquireAllLocks(ref locksAcquired);
                var count = 0;
                foreach (var item in tables.CountPerLock)
                {
                    count = checked(count + item);
                }

                return count;
            }
            finally
            {
                ReleaseLocks(locksAcquired);
            }
        }
    }

    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        return TryGetValueCore(key, GetRangeHash(key), out value);
    }

    /// <summary>
    /// Looks up a key using its precomputed comparer hash.
    /// Key equality distinguishes entries sharing the hash.
    /// </summary>
    public bool TryGetValue(TKey key, uint rangeHash, [MaybeNullWhen(false)] out TValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        return TryGetValueCore(key, rangeHash, out value);
    }

    private bool TryGetValueCore(TKey key, uint rangeHash, [MaybeNullWhen(false)] out TValue value)
    {
        var tables = _tables;
        for (var node = tables.Buckets[rangeHash >> tables.Shift].Node; node is not null; node = node.Next)
        {
            if (KeyEquals(node.Key, key))
            {
                value = node.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    public bool TryAdd(TKey key, TValue value) => TryAddInternal(key, value, updateIfExists: false);

    public bool TryUpdate(TKey key, TValue newValue, TValue comparisonValue)
    {
        ArgumentNullException.ThrowIfNull(key);
        var rangeHash = GetRangeHash(key);
        var tables = _tables;
        while (true)
        {
            ref var bucket = ref GetBucketAndLock(tables, rangeHash, out var lockIndex);
            lock (tables.Locks[lockIndex])
            {
                // A resize can change the bucket's lock. Retry against the published table after taking the lock.
                if (tables != _tables)
                {
                    tables = _tables;
                    continue;
                }

                Node? previous = null;
                for (var node = bucket; node is not null; node = node.Next)
                {
                    if (KeyEquals(node.Key, key))
                    {
                        if (!EqualityComparer<TValue>.Default.Equals(node.Value, comparisonValue))
                        {
                            return false;
                        }

                        SetValue(ref bucket, previous, node, newValue);
                        return true;
                    }

                    previous = node;
                }

                return false;
            }
        }
    }

    public bool TryRemove(TKey key, [MaybeNullWhen(false)] out TValue value)
        => TryRemoveInternal(key, out value, matchValue: false, default);

    /// <summary>Removes the key only when its current value equals the supplied value.</summary>
    public bool TryRemove(KeyValuePair<TKey, TValue> item)
        => TryRemoveInternal(item.Key, out _, matchValue: true, item.Value);

    private bool TryRemoveInternal(TKey key, [MaybeNullWhen(false)] out TValue value, bool matchValue, TValue? comparisonValue)
    {
        ArgumentNullException.ThrowIfNull(key);
        var rangeHash = GetRangeHash(key);
        var tables = _tables;
        while (true)
        {
            ref var bucket = ref GetBucketAndLock(tables, rangeHash, out var lockIndex);
            lock (tables.Locks[lockIndex])
            {
                if (tables != _tables)
                {
                    tables = _tables;
                    continue;
                }

                Node? previous = null;
                for (var node = bucket; node is not null; node = node.Next)
                {
                    if (KeyEquals(node.Key, key))
                    {
                        if (matchValue && !EqualityComparer<TValue>.Default.Equals(node.Value, comparisonValue))
                        {
                            value = default;
                            return false;
                        }

                        if (previous is null)
                        {
                            Volatile.Write(ref bucket, node.Next);
                        }
                        else
                        {
                            previous.Next = node.Next;
                        }

                        value = node.Value;
                        tables.CountPerLock[lockIndex]--;
                        return true;
                    }

                    previous = node;
                }

                value = default;
                return false;
            }
        }
    }

    private bool TryAddInternal(TKey key, TValue value, bool updateIfExists)
    {
        ArgumentNullException.ThrowIfNull(key);
        var rangeHash = GetRangeHash(key);
        var tables = _tables;
        while (true)
        {
            var resize = false;
            ref var bucket = ref GetBucketAndLock(tables, rangeHash, out var lockIndex);
            lock (tables.Locks[lockIndex])
            {
                if (tables != _tables)
                {
                    tables = _tables;
                    continue;
                }

                Node? previous = null;
                for (var node = bucket; node is not null; node = node.Next)
                {
                    if (KeyEquals(node.Key, key))
                    {
                        if (updateIfExists)
                        {
                            SetValue(ref bucket, previous, node, value);
                        }

                        return false;
                    }

                    previous = node;
                }

                var newCount = checked(tables.CountPerLock[lockIndex] + 1);
                var newNode = new Node(key, value, bucket);
                Volatile.Write(ref bucket, newNode);
                tables.CountPerLock[lockIndex] = newCount;
                resize = newCount > _budget;
            }

            // GrowTable acquires locks in order, starting at lock zero, after releasing the insertion lock.
            if (resize)
            {
                GrowTable(tables);
            }

            return true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetValue(ref Node? bucket, Node? previous, Node node, TValue value)
    {
        if (!typeof(TValue).IsValueType || ConcurrentHashRangeDictionaryValue<TValue>.IsWriteAtomic)
        {
            node.Value = value;
        }
        else
        {
            // Readers can still be using this node. Publish a replacement to keep multiword values intact.
            var replacement = new Node(node.Key, value, node.Next);
            if (previous is null)
            {
                Volatile.Write(ref bucket, replacement);
            }
            else
            {
                previous.Next = replacement;
            }
        }
    }

    /// <summary>Enumerates matching entries from one table generation, hashing entries in boundary buckets.</summary>
    public RangeEnumerable EnumerateRange(RingRange range) => new(this, range);

    /// <summary>Enumerates every entry at a hash coordinate, including distinct keys which collide.</summary>
    public RangeEnumerable EnumerateHash(uint rangeHash) => new(this, RingRange.FromPoint(rangeHash));

    /// <summary>
    /// Visits entries synchronously outside collection locks. Callbacks may perform point operations.
    /// Callback exceptions stop visitation and propagate to the caller.
    /// </summary>
    public void VisitRange<TState>(RingRange range, TState state, Action<TState, TKey, TValue> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        foreach (var (key, value) in EnumerateRange(range))
        {
            visitor(state, key, value);
        }
    }

    /// <summary>
    /// Materializes candidates, then conditionally removes them, appending successfully removed entries to
    /// <paramref name="removed"/>. Quiescing writers for the range makes this a complete extraction.
    /// </summary>
    /// <remarks>
    /// Each removal is atomic. Candidate collection and result capacity allocation finish before removal begins.
    /// If hashing or comparison throws during removal, prior removals remain available in the caller-owned result.
    /// Expected-value matching follows ordinary key/value removal semantics, including equal replacement values.
    /// </remarks>
    public void RemoveRange(RingRange range, List<KeyValuePair<TKey, TValue>> removed)
    {
        ArgumentNullException.ThrowIfNull(removed);
        var candidates = new List<KeyValuePair<TKey, TValue>>(EnumerateRange(range));
        removed.EnsureCapacity(checked(removed.Count + candidates.Count));
        foreach (var entry in candidates)
        {
            if (TryRemove(entry))
            {
                removed.Add(entry);
            }
        }
    }

    public Enumerator GetEnumerator() => new(this, RingRange.Full);

    IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public readonly struct RangeEnumerable(ConcurrentHashRangeDictionary<TKey, TValue, TComparer> dictionary, RingRange range)
        : IEnumerable<KeyValuePair<TKey, TValue>>
    {
        public Enumerator GetEnumerator() => new(dictionary, range);
        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
    {
        private readonly ConcurrentHashRangeDictionary<TKey, TValue, TComparer> _dictionary;
        private readonly RingRange _range;
        private Tables? _tables;
        private Node? _node;
        private int _nextBucket;
        private int _remainingBuckets;
        private int _firstBucket;
        private int _lastBucket;
        private bool _filterBucket;

        internal Enumerator(ConcurrentHashRangeDictionary<TKey, TValue, TComparer> dictionary, RingRange range)
        {
            _dictionary = dictionary;
            _range = range;
        }

        public KeyValuePair<TKey, TValue> Current { get; private set; }
        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (_tables is null)
            {
                _tables = _dictionary._tables;
                (_nextBucket, _remainingBuckets) = GetRangeBuckets(_tables, _range);
                _firstBucket = _nextBucket;
                _lastBucket = (int)(_range.End >> _tables.Shift);
            }

            while (true)
            {
                while (_node is { } node)
                {
                    _node = node.Next;
                    if (!_filterBucket || _range.Contains(_dictionary.GetRangeHash(node.Key)))
                    {
                        Current = KeyValuePair.Create(node.Key, node.Value);
                        return true;
                    }
                }

                if (_remainingBuckets == 0)
                {
                    return false;
                }

                _node = _tables.Buckets[_nextBucket].Node;
                _filterBucket = !_range.IsFull && (_nextBucket == _firstBucket || _nextBucket == _lastBucket);
                _nextBucket = (_nextBucket + 1) & (_tables.Buckets.Length - 1);
                _remainingBuckets--;
            }
        }

        public void Reset()
        {
            _tables = null;
            _node = null;
            _nextBucket = 0;
            _remainingBuckets = 0;
            _firstBucket = 0;
            _lastBucket = 0;
            _filterBucket = false;
            Current = default;
        }

        public void Dispose() { }
    }

    private static (int First, int Count) GetRangeBuckets(Tables tables, RingRange range)
    {
        if (range.IsEmpty)
        {
            return (0, 0);
        }

        if (range.IsFull)
        {
            return (0, tables.Buckets.Length);
        }

        var firstHash = unchecked(range.Start + 1);
        var bucketSize = 1UL << tables.Shift;
        var length = unchecked(range.End - range.Start);
        // A wrapped range can intersect the same boundary bucket at both ends. Visit it once.
        var count = (int)Math.Min(
            (ulong)tables.Buckets.Length,
            ((firstHash & (bucketSize - 1)) + length + bucketSize - 1) >> tables.Shift);
        return ((int)(firstHash >> tables.Shift), count);
    }

    private void GrowTable(Tables tables)
    {
        var locksAcquired = 0;
        try
        {
            Monitor.Enter(_tables.Locks[0]);
            locksAcquired = 1;
            if (tables != _tables)
            {
                return;
            }

            long count = 0;
            foreach (var item in tables.CountPerLock)
            {
                count += item;
            }

            if (count < tables.Buckets.Length / 4)
            {
                _budget = (int)Math.Min(int.MaxValue, 2L * _budget);
                return;
            }

            if (tables.Buckets.Length == MaxCapacity)
            {
                _budget = int.MaxValue;
                return;
            }

            var locks = tables.Locks;
            if (_growLocks && locks.Length < MaxLockCount)
            {
                locks = new object[tables.Locks.Length * 2];
                Array.Copy(tables.Locks, locks, tables.Locks.Length);
                for (var i = tables.Locks.Length; i < locks.Length; i++)
                {
                    locks[i] = new object();
                }
            }

            var newTables = new Tables(new VolatileNode[tables.Buckets.Length * 2], locks, new int[locks.Length]);
            AcquireRemainingLocks(tables, ref locksAcquired);
            foreach (var bucket in tables.Buckets)
            {
                for (var node = bucket.Node; node is not null; node = node.Next)
                {
                    ref var newBucket = ref GetBucketAndLock(newTables, GetRangeHash(node.Key), out var lockIndex);
                    newBucket = new Node(node.Key, node.Value, newBucket);
                    newTables.CountPerLock[lockIndex]++;
                }
            }

            _budget = Math.Max(1, newTables.Buckets.Length / locks.Length);
            _tables = newTables;
        }
        finally
        {
            ReleaseLocks(locksAcquired);
        }
    }

    private Tables AcquireAllLocks(ref int locksAcquired)
    {
        Monitor.Enter(_tables.Locks[0]);
        locksAcquired = 1;
        var tables = _tables;
        AcquireRemainingLocks(tables, ref locksAcquired);
        return tables;
    }

    private static void AcquireRemainingLocks(Tables tables, ref int locksAcquired)
    {
        for (var i = 1; i < tables.Locks.Length; i++)
        {
            Monitor.Enter(tables.Locks[i]);
            locksAcquired++;
        }
    }

    private void ReleaseLocks(int locksAcquired)
    {
        var locks = _tables.Locks;
        for (var i = 0; i < locksAcquired; i++)
        {
            Monitor.Exit(locks[i]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint GetRangeHash(TKey key)
        => _comparer.GetHashCode(key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool KeyEquals(TKey left, TKey right)
        => _comparer.Equals(left, right);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref Node? GetBucketAndLock(Tables tables, uint rangeHash, out int lockIndex)
    {
        var bucketIndex = (int)(rangeHash >> tables.Shift);
        lockIndex = bucketIndex & (tables.Locks.Length - 1);
        return ref tables.Buckets[bucketIndex].Node;
    }

    private struct VolatileNode
    {
        // A struct wrapper avoids the array covariance check on each volatile reference load.
        internal volatile Node? Node;
    }

    private sealed class Node(TKey key, TValue value, Node? next)
    {
        internal readonly TKey Key = key;
        internal TValue Value = value;
        internal volatile Node? Next = next;
    }

    private sealed class Tables(VolatileNode[] buckets, object[] locks, int[] countPerLock)
    {
        internal readonly VolatileNode[] Buckets = buckets;
        internal readonly object[] Locks = locks;
        internal readonly int[] CountPerLock = countPerLock;
        internal readonly int Shift = 32 - BitOperations.Log2((uint)buckets.Length);
    }
}

internal static class ConcurrentHashRangeDictionaryValue<T>
{
    internal static readonly bool IsWriteAtomic = !typeof(T).IsValueType
        || typeof(T) == typeof(IntPtr)
        || typeof(T) == typeof(UIntPtr)
        || Type.GetTypeCode(typeof(T)) switch
        {
            TypeCode.Boolean or TypeCode.Byte or TypeCode.Char or TypeCode.Int16 or TypeCode.Int32
                or TypeCode.SByte or TypeCode.Single or TypeCode.UInt16 or TypeCode.UInt32 => true,
            TypeCode.Double or TypeCode.Int64 or TypeCode.UInt64 => IntPtr.Size == 8,
            _ => false,
        };
}
