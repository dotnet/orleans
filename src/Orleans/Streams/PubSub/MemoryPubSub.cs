using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Streams.PubSub
{
    internal class MemoryPubSub : IStreamPubSub
    {
        private MemoryPubSubStore store;
        public MemoryPubSub()
        {
            store = new MemoryPubSubStore();
        }

        public Task<ISet<PubSubSubscriptionState>> RegisterProducer(StreamId streamId, string streamProvider,
            IStreamProducerExtension streamProducer)
        {
            return Task.FromResult(store.RegisterProducer(streamId, streamProvider, streamProducer));
        }

        public Task UnregisterProducer(StreamId streamId, string streamProvider, IStreamProducerExtension streamProducer)
        {
            store.UnregisterProducer(streamId, streamProvider, streamProducer);
            return TaskDone.Done;
        }

        public Task RegisterConsumer(GuidId subscriptionId, StreamId streamId, string streamProvider,
            IStreamConsumerExtension streamConsumer, IStreamFilterPredicateWrapper filter)
        {
            return store.RegisterConsumer(subscriptionId, streamId, streamProvider, streamConsumer, filter);
        }

        public Task UnregisterConsumer(GuidId subscriptionId, StreamId streamId, string streamProvider)
        {
            return store.UnregisterConsumer(subscriptionId, streamId, streamProvider);
        }

        public Task<int> ProducerCount(Guid streamId, string streamProvider, string streamNamespace)
        {
            return Task.FromResult(store.ProducerCount(streamId, streamProvider, streamNamespace));
        }

        public Task<int> ConsumerCount(Guid streamId, string streamProvider, string streamNamespace)
        {
            return Task.FromResult(store.ConsumerCount(streamId, streamProvider, streamNamespace));
        }

        public Task<List<GuidId>> GetAllSubscriptions(StreamId streamId, IStreamConsumerExtension streamConsumer)
        {
            return Task.FromResult(store.GetAllSubscriptions(streamId, streamConsumer));
        }

        public GuidId CreateSubscriptionId(StreamId streamId, IStreamConsumerExtension streamConsumer)
        {
            Guid subscriptionId = Guid.NewGuid();
            return GuidId.GetGuidId(subscriptionId);
        }

        public Task<bool> FaultSubscription(StreamId streamId, GuidId subscriptionId)
        {
            return Task.FromResult(store.FaultSubscription(streamId, subscriptionId));
        }
    }
}
