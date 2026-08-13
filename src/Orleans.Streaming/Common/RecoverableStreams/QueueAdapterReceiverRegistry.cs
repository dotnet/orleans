using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private readonly ConcurrentDictionary<QueueId, TReceiver> _receivers = new();
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
        public IReadOnlyDictionary<QueueId, TReceiver> Receivers => _receivers;

        /// <summary>
        /// Gets or creates the coordinator for a queue.
        /// </summary>
        public TReceiver GetOrCreate(QueueId queueId) => _receivers.GetOrAdd(queueId, _factory);

        /// <summary>
        /// Removes a receiver if it is still the registered instance for the queue.
        /// </summary>
        public bool Remove(QueueId queueId, TReceiver receiver)
            => ((ICollection<KeyValuePair<QueueId, TReceiver>>)_receivers)
                .Remove(new(queueId, receiver));
    }
}
