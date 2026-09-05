using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Streams.Core;

namespace Orleans.Streams
{
    internal interface IPubSubRendezvousGrain : IGrainWithStringKey
    {
        [Alias("B5FFB7F3")]
        Task<ISet<PubSubSubscriptionState>> RegisterProducer(
            QualifiedStreamId streamId,
            GrainId streamProducer,
            MembershipVersion membershipVersion = default,
            CancellationToken cancellationToken = default);

        [Alias("C017B47D")]
        Task UnregisterProducer(QualifiedStreamId streamId, GrainId streamProducer, CancellationToken cancellationToken = default);

        [Alias("5E7E20BC")]
        Task RegisterConsumer(GuidId subscriptionId, QualifiedStreamId streamId, GrainId streamConsumer, string? filterData, CancellationToken cancellationToken = default);

        [Alias("974334B6")]
        Task UnregisterConsumer(GuidId subscriptionId, QualifiedStreamId streamId, CancellationToken cancellationToken = default);

        [Alias("29B61035")]
        Task<int> ProducerCount(QualifiedStreamId streamId, CancellationToken cancellationToken = default);

        [Alias("5F72C5CF")]
        Task<int> ConsumerCount(QualifiedStreamId streamId, CancellationToken cancellationToken = default);

        [Alias("8A033955")]
        Task<PubSubSubscriptionState[]> DiagGetConsumers(QualifiedStreamId streamId, CancellationToken cancellationToken = default);

        [Alias("20AA72BF")]
        Task Validate(CancellationToken cancellationToken = default);

        [Alias("7DBE84FA")]
        Task<List<StreamSubscription>> GetAllSubscriptions(QualifiedStreamId streamId, GrainId streamConsumer = default, CancellationToken cancellationToken = default);

        [Alias("2821FCF5")]
        Task FaultSubscription(GuidId subscriptionId, CancellationToken cancellationToken = default);
    }
}
