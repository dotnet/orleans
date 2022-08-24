using System;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.BroadcastChannel
{
    public interface IBroadcastChannelSubscription
    {
        public ChannelId ChannelId { get; }

        public string ProviderName { get; }

        Task Attach<T>(Func<T, Task> onPublished, Func<Exception, Task> onError = null);
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

        public Task Attach<T>(Func<T, Task> onPublished, Func<Exception, Task> onError = null)
        {
            _consumerExtension.Attach(_streamId, onPublished, onError);
            return Task.CompletedTask;
        }
    }
}

