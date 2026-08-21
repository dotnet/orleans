using System;
using System.Collections.Generic;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime;

/// <summary>
/// Provides thread-local pooling for <see cref="CallbackData"/> instances.
/// </summary>
internal static class CallbackDataPool
{
    private const int MaxPoolSizePerThread = 128;

    [ThreadStatic]
    private static Stack<CallbackData>? _callbacks;

    public static CallbackDataOwner Rent(
        SharedCallbackData shared,
        IResponseCompletionSource context,
        Message message,
        ApplicationRequestInstruments applicationRequestInstruments)
    {
        var callbacks = _callbacks ??= new();
        var callback = callbacks.TryPop(out var pooled) ? pooled : new CallbackData();
        callback.Initialize(shared, context, message, applicationRequestInstruments);
        return new(callback);
    }

    public static void Return(CallbackDataOwner owner) => owner.Release();

    internal static void ReturnCore(CallbackData callback)
    {
        callback.Reset();

        var callbacks = _callbacks ??= new();
        if (callbacks.Count < MaxPoolSizePerThread)
        {
            callbacks.Push(callback);
        }
    }
}
