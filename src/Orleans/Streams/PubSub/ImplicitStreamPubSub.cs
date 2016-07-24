using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Streams
{
    internal class ImplicitStreamPubSub : IStreamPubSub
    {
        private readonly ImplicitStreamSubscriberTable implicitTable;

        public ImplicitStreamPubSub(ImplicitStreamSubscriberTable implicitPubSubTable)
        {
            if (implicitPubSubTable == null)
            {
                throw new ArgumentNullException("implicitPubSubTable");
            }

            this.implicitTable = implicitPubSubTable;
        }

        public Task<ISet<PubSubSubscriptionState>> RegisterProducer(StreamId streamId, string streamProvider, IStreamProducerExtension streamProducer)
        {
            ISet<PubSubSubscriptionState> result = new HashSet<PubSubSubscriptionState>();
            if (String.IsNullOrWhiteSpace(streamId.Namespace)) return Task.FromResult(result);

            IDictionary<Guid, IStreamConsumerExtension> implicitSubscriptions = implicitTable.GetImplicitSubscribers(streamId);
            foreach (var kvp in implicitSubscriptions)
            {
                GuidId subscriptionId = GuidId.GetGuidId(kvp.Key);
                result.Add(new PubSubSubscriptionState(subscriptionId, streamId, kvp.Value));
            }
            return Task.FromResult(result);
        }

        public Task UnregisterProducer(StreamId streamId, string streamProvider, IStreamProducerExtension streamProducer)
        {
            return TaskDone.Done;
        }

        public Task RegisterConsumer(GuidId subscriptionId, StreamId streamId, string streamProvider, IStreamConsumerExtension streamConsumer, IStreamFilterPredicateWrapper filter)
        {
            if (!IsImplicitSubscriber(streamConsumer, streamId))
            {
                throw new ArgumentOutOfRangeException(streamId.ToString(), "Only implicit subscriptions are supported.");
            }
            return TaskDone.Done;
        }

        public Task UnregisterConsumer(GuidId subscriptionId, StreamId streamId, string streamProvider)
        {
            return TaskDone.Done;
        }

        public Task<int> ProducerCount(Guid streamId, string streamProvider, string streamNamespace)
        {
            return Task.FromResult(0);
        }

        public Task<int> ConsumerCount(Guid streamId, string streamProvider, string streamNamespace)
        {
            return Task.FromResult(0);
        }

        public Task<List<GuidId>> GetAllSubscriptions(StreamId streamId, IStreamConsumerExtension streamConsumer)
        {
            return Task.FromResult(new List<GuidId> { CreateSubscriptionId(streamId, streamConsumer) });
        }

        internal bool IsImplicitSubscriber(IAddressable addressable, StreamId streamId)
        {
            return implicitTable.IsImplicitSubscriber(GrainExtensions.GetGrainId(addressable), streamId);
        }

        internal bool IsImplicitSubscriber(GuidId subscriptionId, StreamId streamId)
        {
            return SubscriptionMarker.IsImplicitSubscription(subscriptionId.Guid);
        }

        public GuidId CreateSubscriptionId(StreamId streamId, IStreamConsumerExtension streamConsumer)
        {
            GrainId grainId = GrainExtensions.GetGrainId(streamConsumer);
            Guid subscriptionGuid;
            if (!implicitTable.TryGetImplicitSubscriptionGuid(grainId, streamId, out subscriptionGuid))
            {
                throw new ArgumentOutOfRangeException(streamId.ToString(), "Only implicit subscriptions are supported.");
            }
            return GuidId.GetGuidId(subscriptionGuid);
        }

        public Task<bool> FaultSubscription(StreamId streamId, GuidId subscriptionId)
        {
            return Task.FromResult(false);
        }
    }
}
