using System;
using System.Diagnostics.CodeAnalysis;

namespace Orleans.Runtime;

internal sealed class CallbackRegistry
{
    // MessageFactory is a singleton and assigns host-unique correlation ids.
    private readonly StripedCallbackDictionary<CallbackData> _callbacks = new();

    internal int Count => _callbacks.Count;

    public bool TryRegister(CallbackData callback)
    {
        if (_callbacks.TryAdd(callback.Message.Id, callback, out var isClosed))
        {
            return true;
        }

        if (isClosed)
        {
            return false;
        }

        throw new InvalidOperationException($"A callback with id '{callback.Message.Id}' is already registered.");
    }

    public void Close() => _callbacks.Close();

    public bool TryCompleteResponse(Message response)
    {
        if (response.ResponseTarget is CallbackData directCallback)
        {
            var completed = directCallback.TryDoCallback(response);
            TryRemove(directCallback);
            return completed;
        }

        if (_callbacks.TryRemove(response.Id, out var callback))
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

        return _callbacks.TryGetValue(response.Id, out callback);
    }

    public bool TryRemove(CallbackData callback)
        => _callbacks.TryRemove(callback.Message.Id, callback);

    public int CountWhere<TState>(TState state, Func<CallbackData, TState, bool> predicate)
    {
        return _callbacks.CountWhere(state, predicate);
    }

    public void ForEach<TState>(TState state, Action<CallbackData, TState> action)
    {
        _callbacks.ForEach(state, action);
    }

}
