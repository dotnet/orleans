using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Ensures receiver and cache factories share one coordinator instance per queue.
    /// </summary>
    /// <typeparam name="TReceiver">The combined receiver and cache type.</typeparam>
    public sealed class QueueAdapterReceiverRegistry<TReceiver>
        where TReceiver : class, IQueueAdapterReceiver, IQueueCache
    {
        private readonly ConcurrentDictionary<QueueId, Lazy<TReceiver>> _receivers = new();
        private readonly Func<QueueId, TReceiver> _factory;

        /// <summary>
        /// Initializes a new instance of the <see cref="QueueAdapterReceiverRegistry{TReceiver}"/> class.
        /// </summary>
        public QueueAdapterReceiverRegistry(Func<QueueId, TReceiver> factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        /// <summary>
        /// Gets the registered receiver instances.
        /// </summary>
        public IReadOnlyDictionary<QueueId, TReceiver> Receivers
            => _receivers
                .Where(pair => pair.Value.IsValueCreated)
                .ToDictionary(pair => pair.Key, pair => pair.Value.Value);

        /// <summary>
        /// Gets or creates the coordinator for a queue.
        /// </summary>
        public TReceiver GetOrCreate(QueueId queueId)
        {
            var receiver = _receivers.GetOrAdd(
                queueId,
                static (id, factory) => new(
                    () => factory(id),
                    LazyThreadSafetyMode.ExecutionAndPublication),
                _factory);
            try
            {
                return receiver.Value;
            }
            catch
            {
                ((ICollection<KeyValuePair<QueueId, Lazy<TReceiver>>>)_receivers)
                    .Remove(new(queueId, receiver));
                throw;
            }
        }

        /// <summary>
        /// Removes a receiver if it is still the registered instance for the queue.
        /// </summary>
        public bool Remove(QueueId queueId, TReceiver receiver)
        {
            if (!_receivers.TryGetValue(queueId, out var registered)
                || !registered.IsValueCreated
                || !ReferenceEquals(registered.Value, receiver))
            {
                return false;
            }

            return ((ICollection<KeyValuePair<QueueId, Lazy<TReceiver>>>)_receivers)
                .Remove(new(queueId, registered));
        }
    }
}
