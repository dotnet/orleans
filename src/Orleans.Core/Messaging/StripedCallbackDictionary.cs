#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime;

/// <summary>
/// A striped dictionary that distributes entries across multiple internal dictionaries
/// to reduce lock contention. The stripe is determined by bits embedded in the CorrelationId.
/// </summary>
/// <typeparam name="TValue">The type of values stored in the dictionary.</typeparam>
internal sealed class StripedCallbackDictionary<TValue> : IEnumerable<KeyValuePair<CorrelationId, TValue>>
{
    /// <summary>
    /// The number of bits used to identify the stripe (stored in the upper bits of the CorrelationId).
    /// </summary>
    public const int StripeBits = 7;

    /// <summary>
    /// The number of stripes (must be a power of 2).
    /// </summary>
    public const int StripeCount = 1 << StripeBits; // 128 stripes

    /// <summary>
    /// Mask to extract the stripe index from the upper bits.
    /// </summary>
    private const long StripeMask = (long)(StripeCount - 1) << (64 - StripeBits);

    /// <summary>
    /// The shift amount to move the stripe bits to the lowest position.
    /// </summary>
    private const int StripeShift = 64 - StripeBits;

    private readonly Stripe[] _stripes;

    public StripedCallbackDictionary()
    {
        _stripes = new Stripe[StripeCount];
        for (int i = 0; i < StripeCount; i++)
        {
            _stripes[i] = new Stripe();
        }
    }

    /// <summary>
    /// Encodes a stripe index into the upper bits of a base value to create a CorrelationId.
    /// </summary>
    /// <param name="baseValue">The base value (e.g., from an incrementing counter XORed with a seed).</param>
    /// <param name="stripeIndex">The stripe index (typically derived from thread id).</param>
    /// <returns>A CorrelationId with the stripe encoded in the upper bits.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static CorrelationId CreateCorrelationId(long baseValue, int stripeIndex)
    {
        // Clear the upper StripeBits of the base value and set the stripe index there
        long maskedBase = baseValue & ~StripeMask;
        long stripeValue = (long)(stripeIndex & (StripeCount - 1)) << StripeShift;
        return new CorrelationId(maskedBase | stripeValue);
    }

    /// <summary>
    /// Extracts the stripe index from a CorrelationId.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetStripeIndex(CorrelationId correlationId)
    {
        return (int)((correlationId.ToInt64() & StripeMask) >>> StripeShift);
    }

    /// <summary>
    /// Gets the stripe index for the current thread. Use this when creating new CorrelationIds.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetCurrentThreadStripeIndex()
    {
        return Environment.CurrentManagedThreadId & (StripeCount - 1);
    }

    /// <summary>
    /// Gets the stripe for the given correlation id.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Stripe GetStripe(CorrelationId correlationId)
    {
        return _stripes[GetStripeIndex(correlationId)];
    }

    /// <summary>
    /// Attempts to add the specified key and value to the dictionary.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAdd(CorrelationId key, TValue value)
    {
        var stripe = GetStripe(key);
        lock (stripe.Lock)
        {
            return stripe.Dictionary.TryAdd(key, value);
        }
    }

    /// <summary>
    /// Attempts to get the value associated with the specified key.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(CorrelationId key, out TValue? value)
    {
        var stripe = GetStripe(key);
        lock (stripe.Lock)
        {
            return stripe.Dictionary.TryGetValue(key, out value);
        }
    }

    /// <summary>
    /// Attempts to remove the value with the specified key.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRemove(CorrelationId key, out TValue? value)
    {
        var stripe = GetStripe(key);
        lock (stripe.Lock)
        {
            return stripe.Dictionary.Remove(key, out value);
        }
    }

    /// <summary>
    /// Gets the approximate total count of items across all stripes.
    /// </summary>
    public int Count
    {
        get
        {
            int count = 0;
            foreach (var stripe in _stripes)
            {
                lock (stripe.Lock)
                {
                    count += stripe.Dictionary.Count;
                }
            }
            return count;
        }
    }

    /// <summary>
    /// Counts items matching a predicate across all stripes.
    /// </summary>
    public int CountWhere(Func<KeyValuePair<CorrelationId, TValue>, bool> predicate)
    {
        int count = 0;
        foreach (var stripe in _stripes)
        {
            lock (stripe.Lock)
            {
                foreach (var kvp in stripe.Dictionary)
                {
                    if (predicate(kvp))
                    {
                        count++;
                    }
                }
            }
        }
        return count;
    }

    /// <summary>
    /// Returns an enumerator that iterates through all items in all stripes.
    /// Note: This takes a snapshot of each stripe under its lock.
    /// </summary>
    public Enumerator GetEnumerator() => new(this);

    IEnumerator<KeyValuePair<CorrelationId, TValue>> IEnumerable<KeyValuePair<CorrelationId, TValue>>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private sealed class Stripe
    {
        public readonly object Lock = new();
        public readonly Dictionary<CorrelationId, TValue> Dictionary = new();
    }

    public struct Enumerator : IEnumerator<KeyValuePair<CorrelationId, TValue>>
    {
        private readonly StripedCallbackDictionary<TValue> _dictionary;
        private int _stripeIndex;
        private List<KeyValuePair<CorrelationId, TValue>>? _currentSnapshot;
        private int _snapshotIndex;

        internal Enumerator(StripedCallbackDictionary<TValue> dictionary)
        {
            _dictionary = dictionary;
            _stripeIndex = -1;
            _currentSnapshot = null;
            _snapshotIndex = -1;
        }

        public KeyValuePair<CorrelationId, TValue> Current => _currentSnapshot![_snapshotIndex];

        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            while (true)
            {
                // Try to advance within current snapshot
                if (_currentSnapshot != null)
                {
                    _snapshotIndex++;
                    if (_snapshotIndex < _currentSnapshot.Count)
                    {
                        return true;
                    }
                }

                // Move to next stripe
                _stripeIndex++;
                if (_stripeIndex >= _dictionary._stripes.Length)
                {
                    _currentSnapshot = null;
                    return false;
                }

                // Take a snapshot of the next stripe
                var stripe = _dictionary._stripes[_stripeIndex];
                lock (stripe.Lock)
                {
                    if (stripe.Dictionary.Count > 0)
                    {
                        _currentSnapshot = new List<KeyValuePair<CorrelationId, TValue>>(stripe.Dictionary);
                        _snapshotIndex = -1;
                    }
                    else
                    {
                        _currentSnapshot = null;
                    }
                }
            }
        }

        public void Reset()
        {
            _stripeIndex = -1;
            _currentSnapshot = null;
            _snapshotIndex = -1;
        }

        public void Dispose()
        {
            _currentSnapshot = null;
        }
    }
}
