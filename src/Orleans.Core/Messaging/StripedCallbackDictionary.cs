#nullable enable
using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime;

/// <summary>
/// A striped dictionary that distributes entries across multiple internal dictionaries
/// to reduce lock contention by hashing correlation ids across stripes.
/// </summary>
/// <typeparam name="TValue">The type of values stored in the dictionary.</typeparam>
internal sealed class StripedCallbackDictionary<TValue> : IEnumerable<TValue>
    where TValue : notnull
{
    private const int StripeBits = 7;
    // Fibonacci hashing spreads sequential and strided ids using one multiply and shift.
    private const ulong HashFactor = 11_400_714_819_323_198_485;

    /// <summary>
    /// The number of stripes.
    /// </summary>
    public const int StripeCount = 1 << StripeBits;

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
    /// Computes the stripe index for a correlation id.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetStripeIndex(CorrelationId correlationId)
        => (int)(unchecked((ulong)correlationId.ToInt64() * HashFactor) >> (64 - StripeBits));

    /// <summary>
    /// Gets the stripe for the given callback id.
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
    public bool TryAdd(GrainId owner, CorrelationId id, TValue value)
    {
        var stripe = GetStripe(id);
        lock (stripe.Lock)
        {
            return stripe.Dictionary.TryAdd(new(owner, id), value);
        }
    }

    /// <summary>
    /// Attempts to get the value associated with the specified key.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(GrainId owner, CorrelationId id, [NotNullWhen(true)] out TValue? value)
    {
        var stripe = GetStripe(id);
        lock (stripe.Lock)
        {
            return stripe.Dictionary.TryGetValue(new(owner, id), out value);
        }
    }

    /// <summary>
    /// Attempts to remove the value with the specified key.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRemove(GrainId owner, CorrelationId id, [NotNullWhen(true)] out TValue? value)
    {
        var stripe = GetStripe(id);
        lock (stripe.Lock)
        {
            return stripe.Dictionary.Remove(new(owner, id), out value);
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
    public int CountWhere(Func<TValue, bool> predicate)
    {
        int count = 0;
        foreach (var stripe in _stripes)
        {
            lock (stripe.Lock)
            {
                foreach (var value in stripe.Dictionary.Values)
                {
                    if (predicate(value))
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

    IEnumerator<TValue> IEnumerable<TValue>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private sealed class Stripe
    {
        public readonly object Lock = new();
        public readonly Dictionary<CallbackKey, TValue> Dictionary = new();
    }

    private readonly record struct CallbackKey(GrainId Owner, CorrelationId Id);

    public sealed class Enumerator : IEnumerator<TValue>
    {
        private readonly StripedCallbackDictionary<TValue> _dictionary;
        private int _stripeIndex;
        private TValue[]? _currentSnapshot;
        private int _snapshotCount;
        private int _snapshotIndex;

        internal Enumerator(StripedCallbackDictionary<TValue> dictionary)
        {
            _dictionary = dictionary;
            _stripeIndex = -1;
            _currentSnapshot = null;
            _snapshotCount = 0;
            _snapshotIndex = -1;
        }

        public TValue Current => _currentSnapshot![_snapshotIndex];

        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            while (true)
            {
                // Try to advance within current snapshot
                if (_currentSnapshot != null)
                {
                    _snapshotIndex++;
                    if (_snapshotIndex < _snapshotCount)
                    {
                        return true;
                    }

                    ReturnSnapshot();
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
                        _currentSnapshot = ArrayPool<TValue>.Shared.Rent(stripe.Dictionary.Count);
                        _snapshotCount = 0;
                        foreach (var value in stripe.Dictionary.Values)
                        {
                            _currentSnapshot[_snapshotCount++] = value;
                        }
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
            ReturnSnapshot();
            _snapshotIndex = -1;
        }

        public void Dispose() => ReturnSnapshot();

        private void ReturnSnapshot()
        {
            if (_currentSnapshot is { } snapshot)
            {
                ArrayPool<TValue>.Shared.Return(
                    snapshot,
                    clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<TValue>());
                _currentSnapshot = null;
                _snapshotCount = 0;
            }
        }
    }
}
