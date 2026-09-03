using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Streams.Core;

namespace Orleans.Streams
{
    internal class GrainBasedPubSubRuntime : IStreamPubSub
    {
        private readonly IGrainFactory grainFactory;

        public GrainBasedPubSubRuntime(IGrainFactory grainFactory)
        {
            this.grainFactory = grainFactory;
        }

        public Task<ISet<PubSubSubscriptionState>> RegisterProducer(QualifiedStreamId streamId, GrainId streamProducer)
            => RegisterProducer(streamId, streamProducer, CancellationToken.None);

        public Task<ISet<PubSubSubscriptionState>> RegisterProducer(QualifiedStreamId streamId, GrainId streamProducer, CancellationToken cancellationToken)
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            return streamRendezvous.RegisterProducer(streamId, streamProducer, cancellationToken);
        }

        public Task UnregisterProducer(QualifiedStreamId streamId, GrainId streamProducer)
            => UnregisterProducer(streamId, streamProducer, CancellationToken.None);

        public Task UnregisterProducer(QualifiedStreamId streamId, GrainId streamProducer, CancellationToken cancellationToken)
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            return streamRendezvous.UnregisterProducer(streamId, streamProducer, cancellationToken);
        }

        public Task RegisterConsumer(GuidId subscriptionId, QualifiedStreamId streamId, GrainId streamConsumer, string? filterData)
            => RegisterConsumer(subscriptionId, streamId, streamConsumer, filterData, CancellationToken.None);

        public Task RegisterConsumer(GuidId subscriptionId, QualifiedStreamId streamId, GrainId streamConsumer, string? filterData, CancellationToken cancellationToken)
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            return streamRendezvous.RegisterConsumer(subscriptionId, streamId, streamConsumer, filterData, cancellationToken);
        }

        public Task UnregisterConsumer(GuidId subscriptionId, QualifiedStreamId streamId)
            => UnregisterConsumer(subscriptionId, streamId, CancellationToken.None);

        public Task UnregisterConsumer(GuidId subscriptionId, QualifiedStreamId streamId, CancellationToken cancellationToken)
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            return streamRendezvous.UnregisterConsumer(subscriptionId, streamId, cancellationToken);
        }

        public Task<int> ProducerCount(QualifiedStreamId streamId)
            => ProducerCount(streamId, CancellationToken.None);

        public Task<int> ProducerCount(QualifiedStreamId streamId, CancellationToken cancellationToken)
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            return streamRendezvous.ProducerCount(streamId, cancellationToken);
        }

        public Task<int> ConsumerCount(QualifiedStreamId streamId)
            => ConsumerCount(streamId, CancellationToken.None);

        public Task<int> ConsumerCount(QualifiedStreamId streamId, CancellationToken cancellationToken)
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            return streamRendezvous.ConsumerCount(streamId, cancellationToken);
        }

        public Task<List<StreamSubscription>> GetAllSubscriptions(QualifiedStreamId streamId, GrainId streamConsumer = default)
            => GetAllSubscriptions(streamId, streamConsumer, CancellationToken.None);

        public Task<List<StreamSubscription>> GetAllSubscriptions(QualifiedStreamId streamId, GrainId streamConsumer, CancellationToken cancellationToken)
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            return streamRendezvous.GetAllSubscriptions(streamId, streamConsumer, cancellationToken);
        }

        private IPubSubRendezvousGrain GetRendezvousGrain(QualifiedStreamId streamId)
        {
            return grainFactory.GetGrain<IPubSubRendezvousGrain>(streamId.ToString());
        }

        public GuidId CreateSubscriptionId(QualifiedStreamId streamId, GrainId streamConsumer)
        {
            Guid subscriptionId = SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid());
            return GuidId.GetGuidId(subscriptionId);
        }

        public async Task<bool> FaultSubscription(QualifiedStreamId streamId, GuidId subscriptionId)
            => await FaultSubscription(streamId, subscriptionId, CancellationToken.None);

        public async Task<bool> FaultSubscription(QualifiedStreamId streamId, GuidId subscriptionId, CancellationToken cancellationToken)
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            await streamRendezvous.FaultSubscription(subscriptionId, cancellationToken);
            return true;
        }
    }
}
