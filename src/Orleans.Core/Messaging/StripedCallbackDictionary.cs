#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Orleans.Runtime;

internal sealed class StripedCallbackDictionary<TValue>
    where TValue : notnull
{
    private const int StripeBits = 7;
    private const ulong HashFactor = 11_400_714_819_323_198_485;
    public const int StripeCount = 1 << StripeBits;
    private readonly Stripe[] _stripes = CreateStripes();
    private int _isClosed;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetStripeIndex(CorrelationId correlationId)
        => (int)(unchecked((ulong)correlationId.ToInt64() * HashFactor) >> (64 - StripeBits));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAdd(CorrelationId id, TValue value)
        => TryAdd(id, value, out _);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAdd(CorrelationId id, TValue value, out bool isClosed)
    {
        var stripe = GetStripe(id);
        lock (stripe.Lock)
        {
            if (Volatile.Read(ref _isClosed) != 0)
            {
                isClosed = true;
                return false;
            }

            isClosed = false;
            return stripe.Dictionary.TryAdd(id, value);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(CorrelationId id, [NotNullWhen(true)] out TValue? value)
    {
        var stripe = GetStripe(id);
        lock (stripe.Lock)
        {
            return stripe.Dictionary.TryGetValue(id, out value);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRemove(CorrelationId id, [NotNullWhen(true)] out TValue? value)
    {
        var stripe = GetStripe(id);
        lock (stripe.Lock)
        {
            return stripe.Dictionary.Remove(id, out value);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRemove(CorrelationId id, TValue value)
    {
        var stripe = GetStripe(id);
        lock (stripe.Lock)
        {
            if (!stripe.Dictionary.TryGetValue(id, out var current)
                || !EqualityComparer<TValue>.Default.Equals(current, value))
            {
                return false;
            }

            return stripe.Dictionary.Remove(id);
        }
    }

    public int Count => CountLocked(0);

    public void Close()
    {
        Volatile.Write(ref _isClosed, 1);
        CloseLocked(0);
    }

    public int CountWhere<TState>(TState state, Func<TValue, TState, bool> predicate)
        => CountWhereLocked(0, state, predicate);

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

                for (var index = 0; index < snapshotCount; index++)
                {
                    action(snapshot[index], state);
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

    private static Stripe[] CreateStripes()
    {
        var result = new Stripe[StripeCount];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = new Stripe();
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Stripe GetStripe(CorrelationId id) => _stripes[GetStripeIndex(id)];

    private int CountLocked(int stripeIndex)
    {
        if (stripeIndex < _stripes.Length)
        {
            lock (_stripes[stripeIndex].Lock)
            {
                return CountLocked(stripeIndex + 1);
            }
        }

        var result = 0;
        foreach (var stripe in _stripes)
        {
            result += stripe.Dictionary.Count;
        }

        return result;
    }

    private void CloseLocked(int stripeIndex)
    {
        if (stripeIndex >= _stripes.Length)
        {
            return;
        }

        lock (_stripes[stripeIndex].Lock)
        {
            CloseLocked(stripeIndex + 1);
        }
    }

    private int CountWhereLocked<TState>(
        int stripeIndex,
        TState state,
        Func<TValue, TState, bool> predicate)
    {
        if (stripeIndex < _stripes.Length)
        {
            lock (_stripes[stripeIndex].Lock)
            {
                return CountWhereLocked(stripeIndex + 1, state, predicate);
            }
        }

        var result = 0;
        foreach (var stripe in _stripes)
        {
            foreach (var value in stripe.Dictionary.Values)
            {
                if (predicate(value, state))
                {
                    result++;
                }
            }
        }

        return result;
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
