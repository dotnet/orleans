using System;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Streams
{
    // This is the extension interface for stream consumers
    internal interface IStreamConsumerExtension : IGrainExtension
    {
        Task<StreamHandshakeToken> DeliverImmutable(GuidId subscriptionId, Immutable<object> item, StreamSequenceToken currentToken, StreamHandshakeToken handshakeToken);
        Task<StreamHandshakeToken> DeliverMutable(GuidId subscriptionId, object item, StreamSequenceToken currentToken, StreamHandshakeToken handshakeToken);
        Task<StreamHandshakeToken> DeliverBatch(GuidId subscriptionId, Immutable<IBatchContainer> item, StreamHandshakeToken handshakeToken);
        Task CompleteStream(GuidId subscriptionId);
        Task ErrorInStream(GuidId subscriptionId, Exception exc);
        Task<StreamHandshakeToken> GetSequenceToken(GuidId subscriptionId);
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
