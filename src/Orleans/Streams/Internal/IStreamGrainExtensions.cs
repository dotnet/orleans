using System;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Streams
{
    // This is the extension interface for stream consumers
    internal interface IStreamConsumerExtension : IGrainExtension
    {
        Task<StreamHandshakeToken> DeliverImmutable(GuidId subscriptionId, StreamId streamId, Immutable<object> item, StreamSequenceToken currentToken, StreamHandshakeToken handshakeToken);
        Task<StreamHandshakeToken> DeliverMutable(GuidId subscriptionId, StreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken handshakeToken);
        Task<StreamHandshakeToken> DeliverBatch(GuidId subscriptionId, StreamId streamId, Immutable<IBatchContainer> item, StreamHandshakeToken handshakeToken);
        Task CompleteStream(GuidId subscriptionId);
        Task ErrorInStream(GuidId subscriptionId, Exception exc);
        Task<StreamHandshakeToken> GetSequenceToken(GuidId subscriptionId);
        /// <summary>
        /// Define action to take when subscription changes from outside grain context
        /// </summary>
        /// <typeparam name="T">Type the stream subscription handle is handling</typeparam>
        /// <param name="onAdd">delegate which will be executed when subscription added</param>
        /// <param name="onRemove">delegate which will be executed when subscription removed. Parameter of the delegate is streamProviderName, stream identity and subscription Id </param>
        Task OnSubscriptionChange<T>(Func<StreamSubscriptionHandle<T>, Task> onAdd, Func<string, IStreamIdentity, Guid, Task> onRemove = null);
        Task<bool> RemoveObserver(GuidId subscriptionId, StreamId streamId);
    }

    // This is the extension interface for stream producers
    internal interface IStreamProducerExtension : IGrainExtension
    {
        [AlwaysInterleave]
        Task AddSubscriber(GuidId subscriptionId, StreamId streamId, IStreamConsumerExtension streamConsumer, IStreamFilterPredicateWrapper filter);

        [AlwaysInterleave]
        Task RemoveSubscriber(GuidId subscriptionId, StreamId streamId);
    }
}
