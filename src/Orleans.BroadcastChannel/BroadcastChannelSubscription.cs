using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.BroadcastChannel
{
    /// <summary>
    /// Represents a grain's implicit subscription to a broadcast channel.
    /// </summary>
    public interface IBroadcastChannelSubscription
    {
        /// <summary>
        /// Gets the channel identifier.
        /// </summary>
        public ChannelId ChannelId { get; }

        /// <summary>
        /// Gets the name of the broadcast channel provider.
        /// </summary>
        public string ProviderName { get; }

        /// <summary>
        /// Attaches handlers which receive items and delivery errors for this subscription.
        /// </summary>
        /// <typeparam name="T">The channel item type.</typeparam>
        /// <param name="onPublished">The handler invoked for each published item.</param>
        /// <param name="onError">The optional handler invoked when item delivery fails.</param>
        /// <returns>A task which completes when the handlers have been attached.</returns>
        Task Attach<T>(Func<T, Task> onPublished, Func<Exception, Task>? onError = null);

        /// <summary>
        /// Attaches cancellation-aware callbacks to the channel subscription.
        /// </summary>
        /// <typeparam name="T">The channel element type.</typeparam>
        /// <param name="onPublished">The callback invoked when an element is published.</param>
        /// <param name="onError">The callback invoked when delivery fails.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task Attach<T>(
            Func<T, CancellationToken, Task> onPublished,
            Func<Exception, CancellationToken, Task>? onError = null) =>
            Attach<T>(
                item => onPublished(item, CancellationToken.None),
                onError is null ? null : exception => onError(exception, CancellationToken.None));
    }

    /// <summary>
    /// Receives notification when a grain's implicit broadcast channel subscription is activated.
    /// </summary>
    public interface IOnBroadcastChannelSubscribed
    {
        /// <summary>
        /// Attaches handlers to an activated broadcast channel subscription.
        /// </summary>
        /// <param name="streamSubscription">The activated subscription.</param>
        /// <returns>A task which completes when subscription initialization has completed.</returns>
        public Task OnSubscribed(IBroadcastChannelSubscription streamSubscription);
    }

    internal class BroadcastChannelSubscription : IBroadcastChannelSubscription
    {
        private readonly BroadcastChannelConsumerExtension _consumerExtension;
        private readonly InternalChannelId _streamId;

        public ChannelId ChannelId => _streamId.ChannelId;

        public string ProviderName => _streamId.ProviderName;

        public BroadcastChannelSubscription(BroadcastChannelConsumerExtension consumerExtension, InternalChannelId streamId)
        {
            _consumerExtension = consumerExtension;
            _streamId = streamId;
        }

        public Task Attach<T>(Func<T, Task> onPublished, Func<Exception, Task>? onError = null)
        {
            _consumerExtension.Attach(_streamId, onPublished, onError);
            return Task.CompletedTask;
        }

        public Task Attach<T>(
            Func<T, CancellationToken, Task> onPublished,
            Func<Exception, CancellationToken, Task>? onError = null)
        {
            _consumerExtension.Attach(_streamId, onPublished, onError);
            return Task.CompletedTask;
        }
    }
}
