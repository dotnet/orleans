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

        public Task<ISet<PubSubSubscriptionState>> RegisterProducer(InternalStreamId streamId, IStreamProducerExtension streamProducer)
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            return streamRendezvous.RegisterProducer(streamId, streamProducer);
        }

        public Task UnregisterProducer(InternalStreamId streamId, IStreamProducerExtension streamProducer)
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            return streamRendezvous.UnregisterProducer(streamId, streamProducer);
        }

        public Task RegisterConsumer(GuidId subscriptionId, InternalStreamId streamId, IStreamConsumerExtension streamConsumer, string filterData)
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            return streamRendezvous.RegisterConsumer(subscriptionId, streamId, streamConsumer, filterData);
        }

        public Task UnregisterConsumer(GuidId subscriptionId, InternalStreamId streamId)
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            return streamRendezvous.UnregisterConsumer(subscriptionId, streamId);
        }

        public Task<int> ProducerCount(InternalStreamId streamId)
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            return streamRendezvous.ProducerCount(streamId);
        }

        public Task<int> ConsumerCount(InternalStreamId streamId)
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            return streamRendezvous.ConsumerCount(streamId);
        }

        public Task<List<StreamSubscription>> GetAllSubscriptions(InternalStreamId streamId, IStreamConsumerExtension streamConsumer = null)
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            return streamRendezvous.GetAllSubscriptions(streamId, streamConsumer);
        }

        private IPubSubRendezvousGrain GetRendezvousGrain(InternalStreamId streamId)
        {
            return grainFactory.GetGrain<IPubSubRendezvousGrain>(streamId.ToString());
        }

        public GuidId CreateSubscriptionId(InternalStreamId streamId, IStreamConsumerExtension streamConsumer)
        {
            Guid subscriptionId = SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid());
            return GuidId.GetGuidId(subscriptionId);
        }

        public async Task<bool> FaultSubscription(InternalStreamId streamId, GuidId subscriptionId)
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            await streamRendezvous.FaultSubscription(subscriptionId);
            return true;
        }
    }
}
