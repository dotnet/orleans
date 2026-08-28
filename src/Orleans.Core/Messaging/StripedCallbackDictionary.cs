#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime;

/// <summary>
/// A striped dictionary that distributes entries across multiple internal dictionaries
/// to reduce lock contention by hashing correlation ids across stripes.
/// </summary>
/// <typeparam name="TValue">The type of values stored in the dictionary.</typeparam>
internal sealed class StripedCallbackDictionary<TValue>
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
    public bool TryAdd(CorrelationId id, TValue value)
    {
        var stripe = GetStripe(id);
        lock (stripe.Lock)
        {
            return stripe.Dictionary.TryAdd(id, value);
        }
    }

    /// <summary>
    /// Attempts to get the value associated with the specified key.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(CorrelationId id, [NotNullWhen(true)] out TValue? value)
    {
        var stripe = GetStripe(id);
        lock (stripe.Lock)
        {
            return stripe.Dictionary.TryGetValue(id, out value);
        }
    }

    /// <summary>
    /// Attempts to remove the value with the specified key.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRemove(CorrelationId id, [NotNullWhen(true)] out TValue? value)
    {
        var stripe = GetStripe(id);
        lock (stripe.Lock)
        {
            return stripe.Dictionary.Remove(id, out value);
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
    /// Visits a snapshot of the values in each stripe.
    /// </summary>
    public void ForEach<TState>(TState state, Action<TValue, TState> action)
    {
        foreach (var stripe in _stripes)
        {
            TValue[]? snapshot = null;
            var snapshotCount = 0;
            try
            {
                lock (stripe.Lock)
                {
                    if (stripe.Dictionary.Count == 0)
                    {
                        continue;
                    }

                    snapshot = ArrayPool<TValue>.Shared.Rent(stripe.Dictionary.Count);
                    foreach (var value in stripe.Dictionary.Values)
                    {
                        snapshot[snapshotCount++] = value;
                    }
                }

                for (var i = 0; i < snapshotCount; i++)
                {
                    action(snapshot[i], state);
                }
            }
            finally
            {
                if (snapshot is not null)
                {
                    ArrayPool<TValue>.Shared.Return(
                        snapshot,
                        clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<TValue>());
                }
            }
        }
    }

    private sealed class Stripe
    {
#if NET9_0_OR_GREATER
        public readonly System.Threading.Lock Lock = new();
#else
        public readonly object Lock = new();
#endif
        public readonly Dictionary<CorrelationId, TValue> Dictionary = new();
    }
}
