using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Orleans.Runtime;

internal sealed class CallbackRegistry
{
    private readonly ConcurrentDictionary<(GrainId, CorrelationId), CallbackData> _callbacks = new();

    internal int Count => _callbacks.Count;

    public bool TryAdd(CallbackData callback)
        => _callbacks.TryAdd((callback.Message.SendingGrain, callback.Message.Id), callback);

    public bool TryCompleteResponse(Message response)
    {
        if (response.ResponseTarget is CallbackData directCallback)
        {
            var completed = directCallback.TryDoCallback(response);
            TryRemove(directCallback);
            return completed;
        }

        if (_callbacks.TryRemove((response.TargetGrain, response.Id), out var callback))
        {
            return callback.TryDoCallback(response);
        }

        return false;
    }

    public bool TryGetResponseCallback(Message response, [NotNullWhen(true)] out CallbackData? callback)
    {
        if (response.ResponseTarget is CallbackData directCallback)
        {
            if (!directCallback.IsCompleted)
            {
                callback = directCallback;
                return true;
            }

            callback = null;
            return false;
        }

        return _callbacks.TryGetValue((response.TargetGrain, response.Id), out callback);
    }

    public bool TryRemove(CallbackData callback)
        => _callbacks.TryRemove(KeyValuePair.Create(
            (callback.Message.SendingGrain, callback.Message.Id),
            callback));

    public int CountWhere<TState>(TState state, Func<CallbackData, TState, bool> predicate)
    {
        var result = 0;
        foreach (var callback in _callbacks.Values)
        {
            if (predicate(callback, state))
            {
                result++;
            }
        }

        return result;
    }

    public void ForEach<TState>(TState state, Action<CallbackData, TState> action)
    {
        foreach (var callback in _callbacks.Values)
        {
            action(callback, state);
        }
    }
}
