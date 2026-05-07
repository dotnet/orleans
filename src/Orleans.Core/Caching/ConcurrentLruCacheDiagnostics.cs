using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Orleans.Caching;

internal static class ConcurrentLruCacheDiagnostics
{
    internal const string ListenerName = "Orleans.Caching.ConcurrentLruCache";
    internal const string ExpiredItemsRemovedEventName = nameof(ExpiredItemsRemoved);

    private static readonly DiagnosticListener Listener = new(ListenerName);

    internal static IObservable<ExpiredItemsRemoved> ExpiredItemsRemovedEvents { get; } = new Observable();

    internal sealed class ExpiredItemsRemoved(object cache, int removedCount)
    {
        public object Cache { get; } = cache;

        public int RemovedCount { get; } = removedCount;
    }

    internal static void EmitExpiredItemsRemoved(object cache, int removedCount)
    {
        if (!Listener.IsEnabled(ExpiredItemsRemovedEventName))
        {
            return;
        }

        Emit(cache, removedCount);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(object cache, int removedCount)
        {
            Listener.Write(ExpiredItemsRemovedEventName, new ExpiredItemsRemoved(cache, removedCount));
        }
    }

    private sealed class Observable : IObservable<ExpiredItemsRemoved>
    {
        public IDisposable Subscribe(IObserver<ExpiredItemsRemoved> observer) => Listener.Subscribe(new Observer(observer));

        private sealed class Observer(IObserver<ExpiredItemsRemoved> observer) : IObserver<KeyValuePair<string, object?>>
        {
            public void OnCompleted() => observer.OnCompleted();

            public void OnError(Exception error) => observer.OnError(error);

            public void OnNext(KeyValuePair<string, object?> value)
            {
                if (value.Value is ExpiredItemsRemoved evt)
                {
                    observer.OnNext(evt);
                }
            }
        }
    }
}
