using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Streams
{
    // This is the extension interface for stream consumers
    internal interface IStreamConsumerExtension : IGrainExtension
    {
        [Alias("6D8FAEB2")]
        Task<StreamHandshakeToken?> DeliverImmutable(GuidId subscriptionId, QualifiedStreamId streamId, [Immutable] object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken = default);
        [Alias("31840DDE")]
        Task<StreamHandshakeToken?> DeliverMutable(GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken = default);
        [Alias("B9CFF2C9")]
        Task<StreamHandshakeToken?> DeliverBatch(GuidId subscriptionId, QualifiedStreamId streamId, [Immutable] IBatchContainer item, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken = default);
        [Alias("49F94A48")]
        Task CompleteStream(GuidId subscriptionId, CancellationToken cancellationToken = default);
        [Alias("4C676CAF")]
        Task ErrorInStream(GuidId subscriptionId, Exception exc, CancellationToken cancellationToken = default);
        [Alias("C265B3CB")]
        Task<StreamHandshakeToken?> GetSequenceToken(GuidId subscriptionId, CancellationToken cancellationToken = default);
    }

    // This is the extension interface for stream producers
    internal interface IStreamProducerExtension : IGrainExtension
    {
        [AlwaysInterleave]
        [Alias("1341E3D4")]
        Task AddSubscriber(GuidId subscriptionId, QualifiedStreamId streamId, GrainId streamConsumer, string? filterData, CancellationToken cancellationToken = default);

        [AlwaysInterleave]
        [Alias("B98BA876")]
        Task RemoveSubscriber(GuidId subscriptionId, QualifiedStreamId streamId, CancellationToken cancellationToken = default);
    }
}
