using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Streams
{
    internal class StreamPubSubImpl : IStreamPubSub
    {
        private readonly IStreamPubSub explicitPubSub;
        private readonly ImplicitStreamPubSub implicitPubSub;

        public StreamPubSubImpl(IStreamPubSub explicitPubSub, ImplicitStreamPubSub implicitPubSub)
        {
            if (explicitPubSub == null)
            {
                throw new ArgumentNullException("explicitPubSub");
            }

            if (implicitPubSub == null)
            {
                throw new ArgumentNullException("implicitPubSub");
            }

            this.explicitPubSub = explicitPubSub;
            this.implicitPubSub = implicitPubSub;
        }

        public async Task<ISet<PubSubSubscriptionState>> RegisterProducer(StreamId streamId, string streamProvider, IStreamProducerExtension streamProducer)
        {
            ISet<PubSubSubscriptionState> explicitRes = await explicitPubSub.RegisterProducer(streamId, streamProvider, streamProducer);
            ISet<PubSubSubscriptionState> implicitRes = await implicitPubSub.RegisterProducer(streamId, streamProvider, streamProducer);
            explicitRes.UnionWith(implicitRes);
            return explicitRes;
        }

        public Task UnregisterProducer(StreamId streamId, string streamProvider, IStreamProducerExtension streamProducer)
        {
            return explicitPubSub.UnregisterProducer(streamId, streamProvider, streamProducer);
        }

        public Task RegisterConsumer(GuidId subscriptionId, StreamId streamId, string streamProvider, IStreamConsumerExtension streamConsumer, IStreamFilterPredicateWrapper filter)
        {
            return implicitPubSub.IsImplicitSubscriber(streamConsumer, streamId)
                ? implicitPubSub.RegisterConsumer(subscriptionId, streamId, streamProvider, streamConsumer, filter)
                : explicitPubSub.RegisterConsumer(subscriptionId, streamId, streamProvider, streamConsumer, filter);
        }

        public Task UnregisterConsumer(GuidId subscriptionId, StreamId streamId, string streamProvider)
        {
            return implicitPubSub.IsImplicitSubscriber(subscriptionId, streamId)
                ? implicitPubSub.UnregisterConsumer(subscriptionId, streamId, streamProvider)
                : explicitPubSub.UnregisterConsumer(subscriptionId, streamId, streamProvider);
        }

        public Task<int> ProducerCount(Guid streamId, string streamProvider, string streamNamespace)
        {
            return explicitPubSub.ProducerCount(streamId, streamProvider, streamNamespace); 
        }

        public Task<int> ConsumerCount(Guid streamId, string streamProvider, string streamNamespace)
        {
            return explicitPubSub.ConsumerCount(streamId, streamProvider, streamNamespace); 
        }

        public async Task<List<GuidId>> GetAllSubscriptions(StreamId streamId, IStreamConsumerExtension streamConsumer)
        {
            return implicitPubSub.IsImplicitSubscriber(streamConsumer, streamId)
                ? await implicitPubSub.GetAllSubscriptions(streamId, streamConsumer)
                : await explicitPubSub.GetAllSubscriptions(streamId, streamConsumer);
        }

        public GuidId CreateSubscriptionId(StreamId streamId, IStreamConsumerExtension streamConsumer)
        {
            return implicitPubSub.IsImplicitSubscriber(streamConsumer, streamId)
               ? implicitPubSub.CreateSubscriptionId(streamId, streamConsumer)
               : explicitPubSub.CreateSubscriptionId(streamId, streamConsumer);
        }

        public Task<bool> FaultSubscription(StreamId streamId, GuidId subscriptionId)
        {
            return implicitPubSub.IsImplicitSubscriber(subscriptionId, streamId)
                ? implicitPubSub.FaultSubscription(streamId, subscriptionId)
                : explicitPubSub.FaultSubscription(streamId, subscriptionId);
        }
    }
}
