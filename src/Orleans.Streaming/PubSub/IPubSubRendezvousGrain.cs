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

        Task UnregisterProducer(
            QualifiedStreamId streamId,
            GrainId streamProducer,
            CancellationToken cancellationToken = default);

        Task RegisterConsumer(
            GuidId subscriptionId,
            QualifiedStreamId streamId,
            GrainId streamConsumer,
            string? filterData,
            CancellationToken cancellationToken = default);

        Task UnregisterConsumer(
            GuidId subscriptionId,
            QualifiedStreamId streamId,
            CancellationToken cancellationToken = default);

        Task<int> ProducerCount(
            QualifiedStreamId streamId,
            CancellationToken cancellationToken = default);

        Task<int> ConsumerCount(
            QualifiedStreamId streamId,
            CancellationToken cancellationToken = default);

        Task<PubSubSubscriptionState[]> DiagGetConsumers(
            QualifiedStreamId streamId,
            CancellationToken cancellationToken = default);

        Task Validate(CancellationToken cancellationToken = default);

        Task<List<StreamSubscription>> GetAllSubscriptions(
            QualifiedStreamId streamId,
            GrainId streamConsumer = default,
            CancellationToken cancellationToken = default);

        Task FaultSubscription(
            GuidId subscriptionId,
            CancellationToken cancellationToken = default);
    }
}
