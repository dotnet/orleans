using System;
using System.Collections.Generic;
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
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            return streamRendezvous.RegisterProducer(streamId, streamProducer);
        }

        public Task UnregisterProducer(QualifiedStreamId streamId, GrainId streamProducer)
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            return streamRendezvous.UnregisterProducer(streamId, streamProducer);
        }

        public Task RegisterConsumer(GuidId subscriptionId, QualifiedStreamId streamId, GrainId streamConsumer, string filterData)
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            return streamRendezvous.RegisterConsumer(subscriptionId, streamId, streamConsumer, filterData);
        }

        public Task UnregisterConsumer(GuidId subscriptionId, QualifiedStreamId streamId)
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            return streamRendezvous.UnregisterConsumer(subscriptionId, streamId);
        }

        public Task<int> ProducerCount(QualifiedStreamId streamId)
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            return streamRendezvous.ProducerCount(streamId);
        }

        public Task<int> ConsumerCount(QualifiedStreamId streamId)
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            return streamRendezvous.ConsumerCount(streamId);
        }

        public Task<List<StreamSubscription>> GetAllSubscriptions(QualifiedStreamId streamId, GrainId streamConsumer = default)
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            return streamRendezvous.GetAllSubscriptions(streamId, streamConsumer);
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
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            await streamRendezvous.FaultSubscription(subscriptionId);
            return true;
        }
    }
}
