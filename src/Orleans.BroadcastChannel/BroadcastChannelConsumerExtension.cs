using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.BroadcastChannel
{
    internal interface IBroadcastChannelConsumerExtension : IGrainExtension
    {
        Task OnError(InternalChannelId streamId, Exception exception);
        Task OnPublished(InternalChannelId streamId, object item);
    }

    internal class BroadcastChannelConsumerExtension : IBroadcastChannelConsumerExtension
    {
        private readonly ConcurrentDictionary<InternalChannelId, ICallback> _handlers = new();
        private readonly IOnBroadcastChannelSubscribed _subscriptionObserver;
        private AsyncLock _lock = new AsyncLock();

        private interface ICallback
        {
            Task OnError(Exception exception);

            Task OnPublished(object item);
        }

        private class Callback<T> : ICallback
        {
            private readonly Func<T, Task> _onPublished;
            private readonly Func<Exception, Task> _onError;

            private static Task NoOp(Exception _) => Task.CompletedTask;

            public Callback(Func<T, Task> onPublished, Func<Exception, Task> onError)
            {
                _onPublished = onPublished;
                _onError = onError ?? NoOp;
            }

            public Task OnError(Exception exception) => _onError(exception);

            public Task OnPublished(object item)
            {
                return item is T typedItem
                    ? _onPublished(typedItem)
                    : _onError(new InvalidCastException($"Received an item of type {item.GetType().Name}, expected {typeof(T).FullName}"));
            }
        }

        public BroadcastChannelConsumerExtension(IGrainContextAccessor grainContextAccessor)
        {
            _subscriptionObserver = grainContextAccessor.GrainContext?.GrainInstance as IOnBroadcastChannelSubscribed;
            if (_subscriptionObserver == null)
            {
                throw new ArgumentException($"The grain doesn't implement interface {nameof(IOnBroadcastChannelSubscribed)}");
            }
        }

        public async Task OnError(InternalChannelId streamId, Exception exception)
        {
            var callback = await GetStreamCallback(streamId);
            if (callback != default)
            {
                await callback.OnError(exception);
            }
        }

        public async Task OnPublished(InternalChannelId streamId, object item)
        {
            var callback = await GetStreamCallback(streamId);
            if (callback != default)
            {
                await callback.OnPublished(item);
            }
        }

        public void Attach<T>(InternalChannelId streamId, Func<T, Task> onPublished, Func<Exception, Task> onError)
        {
            _handlers.TryAdd(streamId, new Callback<T>(onPublished, onError));
        }

        private async ValueTask<ICallback> GetStreamCallback(InternalChannelId streamId)
        {
            ICallback callback;
            if (_handlers.TryGetValue(streamId, out callback))
            {
                return callback;
            }
            using (await _lock.LockAsync())
            {
                if (_handlers.TryGetValue(streamId, out callback))
                {
                    return callback;
                }
                // Give a chance to the grain to attach a handler for this streamId
                var subscription = new BroadcastChannelSubscription(this, streamId);
                await _subscriptionObserver.OnSubscribed(subscription);
            }
            _handlers.TryGetValue(streamId, out callback);
            return callback;
        }
    }
}

