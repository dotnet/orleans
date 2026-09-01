using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.BroadcastChannel.Diagnostics;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Orleans.BroadcastChannel
{
    internal interface IBroadcastChannelConsumerExtension : IGrainExtension
    {
        [Alias("73F72B20")]
        Task OnError(InternalChannelId streamId, Exception exception, CancellationToken cancellationToken = default);

        [Alias("B1E55518")]
        Task OnPublished(InternalChannelId streamId, object item, CancellationToken cancellationToken = default);
    }

    internal class BroadcastChannelConsumerExtension : IBroadcastChannelConsumerExtension
    {
        private readonly ConcurrentDictionary<InternalChannelId, ICallback> _handlers = new();
        private readonly IOnBroadcastChannelSubscribed _subscriptionObserver;
        private readonly GrainId _grainId;
        private readonly SiloAddress _siloAddress;
        private readonly string _clusterId;
        private readonly AsyncLock _lock = new AsyncLock();

        private interface ICallback
        {
            Task OnError(Exception exception, CancellationToken cancellationToken);

            Task OnPublished(object item, CancellationToken cancellationToken);
        }

        private class Callback<T> : ICallback
        {
            private readonly Func<T, CancellationToken, Task> _onPublished;
            private readonly Func<Exception, CancellationToken, Task> _onError;

            private static Task NoOp(Exception _, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public Callback(
                Func<T, CancellationToken, Task> onPublished,
                Func<Exception, CancellationToken, Task>? onError)
            {
                _onPublished = onPublished;
                _onError = onError ?? NoOp;
            }

            public Task OnError(Exception exception, CancellationToken cancellationToken) => _onError(exception, cancellationToken);

            public Task OnPublished(object item, CancellationToken cancellationToken)
            {
                return item is T typedItem
                    ? _onPublished(typedItem, cancellationToken)
                    : _onError(
                        new InvalidCastException($"Received an item of type {item.GetType().Name}, expected {typeof(T).FullName}"),
                        cancellationToken);
            }
        }

        public BroadcastChannelConsumerExtension(
            IGrainContextAccessor grainContextAccessor,
            IOptions<ClusterOptions> clusterOptions)
        {
            var grainContext = grainContextAccessor.GrainContext;
            _subscriptionObserver = (grainContext?.GrainInstance as IOnBroadcastChannelSubscribed)!;
            _grainId = grainContext?.GrainId ?? default;
            _siloAddress = grainContext?.Address.SiloAddress ?? throw new ArgumentException("A grain context is required.");
            _clusterId = clusterOptions.Value.ClusterId;
            if (_subscriptionObserver == null)
            {
                throw new ArgumentException($"The grain doesn't implement interface {nameof(IOnBroadcastChannelSubscribed)}");
            }
        }

        public async Task OnError(
            InternalChannelId streamId,
            Exception exception,
            CancellationToken cancellationToken = default)
        {
            var callback = await GetStreamCallback(streamId, cancellationToken);
            if (callback != default)
            {
                await callback.OnError(exception, cancellationToken);
            }
        }

        public async Task OnPublished(
            InternalChannelId streamId,
            object item,
            CancellationToken cancellationToken = default)
        {
            var callback = await GetStreamCallback(streamId, cancellationToken);
            if (callback != default)
            {
                await callback.OnPublished(item, cancellationToken);
                BroadcastChannelEvents.EmitItemDelivered(streamId.ProviderName, streamId.ChannelId, _grainId, _siloAddress, _clusterId);
            }
        }

        public void Attach<T>(InternalChannelId streamId, Func<T, Task> onPublished, Func<Exception, Task>? onError)
            => Attach<T>(
                streamId,
                (item, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return onPublished(item);
                },
                onError is null
                    ? null
                    : (exception, cancellationToken) =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return onError(exception);
                    });

        public void Attach<T>(
            InternalChannelId streamId,
            Func<T, CancellationToken, Task> onPublished,
            Func<Exception, CancellationToken, Task>? onError)
        {
            _handlers.TryAdd(streamId, new Callback<T>(onPublished, onError));
        }

        private async ValueTask<ICallback?> GetStreamCallback(
            InternalChannelId streamId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ICallback? callback;
            if (_handlers.TryGetValue(streamId, out callback))
            {
                return callback;
            }
            using (await _lock.LockAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_handlers.TryGetValue(streamId, out callback))
                {
                    return callback;
                }
                // Give a chance to the grain to attach a handler for this streamId
                var subscription = new BroadcastChannelSubscription(this, streamId);
                await _subscriptionObserver.OnSubscribed(subscription);
                cancellationToken.ThrowIfCancellationRequested();
            }
            _handlers.TryGetValue(streamId, out callback);
            return callback;
        }
    }
}
