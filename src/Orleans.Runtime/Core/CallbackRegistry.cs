using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Orleans.Runtime;

internal sealed class CallbackRegistry
{
    private const int StripeBits = 7;
    private const ulong HashFactor = 11_400_714_819_323_198_485;

    internal const int StripeCount = 1 << StripeBits;

    private readonly Stripe[] _stripes = CreateStripes();
    private int _isClosed;

    internal int Count => CountLocked(0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetStripeIndex(CorrelationId correlationId)
        => (int)(unchecked((ulong)correlationId.ToInt64() * HashFactor) >> (64 - StripeBits));

    // MessageFactory assigns host-unique correlation ids, so the id is sufficient as the registry key.
    public bool TryRegister(CallbackData callback)
    {
        var id = callback.Message.Id;
        var stripe = GetStripe(id);
        lock (stripe.Lock)
        {
            if (Volatile.Read(ref _isClosed) != 0)
            {
                return false;
            }

            if (stripe.Callbacks.TryAdd(id, callback))
            {
                return true;
            }
        }

        throw new InvalidOperationException($"A callback with id '{id}' is already registered.");
    }

    public void Close()
    {
        Volatile.Write(ref _isClosed, 1);
        CloseLocked(0);
    }

    public bool TryCompleteResponse(Message response)
    {
        var stripe = GetStripe(response.Id);
        CallbackData callback;
        lock (stripe.Lock)
        {
            if (!stripe.Callbacks.Remove(response.Id, out callback!))
            {
                return false;
            }
        }

        _ = callback.TryDoCallback(response);
        return true;
    }

    public bool TryGetResponseCallback(Message response, [NotNullWhen(true)] out CallbackData? callback)
    {
        var stripe = GetStripe(response.Id);
        lock (stripe.Lock)
        {
            return stripe.Callbacks.TryGetValue(response.Id, out callback);
        }
    }

    public bool TryRemove(CallbackData callback)
    {
        var id = callback.Message.Id;
        var stripe = GetStripe(id);
        lock (stripe.Lock)
        {
            if (!stripe.Callbacks.TryGetValue(id, out var current) || !ReferenceEquals(current, callback))
            {
                return false;
            }

            return stripe.Callbacks.Remove(id);
        }
    }

    public int CountWhere<TState>(TState state, Func<CallbackData, TState, bool> predicate)
        => CountWhereLocked(0, state, predicate);

    public void ForEach<TState>(TState state, Action<CallbackData, TState> action)
    {
        foreach (var stripe in _stripes)
        {
            CallbackData[]? snapshot = null;
            var snapshotCount = 0;
            try
            {
                lock (stripe.Lock)
                {
                    if (stripe.Callbacks.Count == 0)
                    {
                        continue;
                    }

                    snapshot = ArrayPool<CallbackData>.Shared.Rent(stripe.Callbacks.Count);
                    foreach (var callback in stripe.Callbacks.Values)
                    {
                        snapshot[snapshotCount++] = callback;
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
                    ArrayPool<CallbackData>.Shared.Return(snapshot, clearArray: true);
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
            result += stripe.Callbacks.Count;
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
        Func<CallbackData, TState, bool> predicate)
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
            foreach (var callback in stripe.Callbacks.Values)
            {
                if (predicate(callback, state))
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
        public readonly Dictionary<CorrelationId, CallbackData> Callbacks = new();
    }
}
