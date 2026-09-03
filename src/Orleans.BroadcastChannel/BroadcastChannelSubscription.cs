using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.BroadcastChannel
{
    public interface IBroadcastChannelSubscription
    {
        public ChannelId ChannelId { get; }

        public string ProviderName { get; }

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

    public interface IOnBroadcastChannelSubscribed
    {
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
