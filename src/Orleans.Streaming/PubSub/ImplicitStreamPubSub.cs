using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Streams.Core;

namespace Orleans.Streams
{
    internal class ImplicitStreamPubSub : IStreamPubSub
    {
        private readonly IInternalGrainFactory grainFactory;
        private readonly ImplicitStreamSubscriberTable implicitTable;

        public ImplicitStreamPubSub(IInternalGrainFactory grainFactory, ImplicitStreamSubscriberTable implicitPubSubTable)
        {
            if (implicitPubSubTable == null)
            {
                throw new ArgumentNullException("implicitPubSubTable");
            }

            this.grainFactory = grainFactory;
            this.implicitTable = implicitPubSubTable;
        }

        public Task<ISet<PubSubSubscriptionState>> RegisterProducer(InternalStreamId streamId, IStreamProducerExtension streamProducer)
        {
            ISet<PubSubSubscriptionState> result = new HashSet<PubSubSubscriptionState>();
            if (!ImplicitStreamSubscriberTable.IsImplicitSubscribeEligibleNameSpace(streamId.GetNamespace())) return Task.FromResult(result);

            IDictionary<Guid, IStreamConsumerExtension> implicitSubscriptions = implicitTable.GetImplicitSubscribers(streamId, this.grainFactory);
            foreach (var kvp in implicitSubscriptions)
            {
                GuidId subscriptionId = GuidId.GetGuidId(kvp.Key);
                result.Add(new PubSubSubscriptionState(subscriptionId, streamId, kvp.Value));
            }
            return Task.FromResult(result);
        }

        public Task UnregisterProducer(InternalStreamId streamId, IStreamProducerExtension streamProducer)
        {
            return Task.CompletedTask;
        }

        public Task RegisterConsumer(GuidId subscriptionId, InternalStreamId streamId, IStreamConsumerExtension streamConsumer, string filterData)
        {
            // TODO BPETIT filter data?
            if (!IsImplicitSubscriber(streamConsumer, streamId))
            {
                throw new ArgumentOutOfRangeException(streamId.ToString(), "Only implicit subscriptions are supported.");
            }
            return Task.CompletedTask;
        }

        public Task UnregisterConsumer(GuidId subscriptionId, InternalStreamId streamId)
        {
            if (!IsImplicitSubscriber(subscriptionId, streamId))
            {
                throw new ArgumentOutOfRangeException(streamId.ToString(), "Only implicit subscriptions are supported.");
            }
            return Task.CompletedTask;
        }

        public Task<int> ProducerCount(InternalStreamId streamId)
        {
            return Task.FromResult(0);
        }

        public Task<int> ConsumerCount(InternalStreamId streamId)
        {
            return Task.FromResult(0);
        }

        public Task<List<StreamSubscription>> GetAllSubscriptions(InternalStreamId streamId, IStreamConsumerExtension streamConsumer = null)
        {
            if (!ImplicitStreamSubscriberTable.IsImplicitSubscribeEligibleNameSpace(streamId.GetNamespace()))
                return Task.FromResult(new List<StreamSubscription>());

            if (streamConsumer != null)
            {
                var subscriptionId = CreateSubscriptionId(streamId, streamConsumer);
                var grainId = streamConsumer as GrainReference;
                return Task.FromResult(new List<StreamSubscription>
                { new StreamSubscription(subscriptionId.Guid, streamId.ProviderName, streamId, grainId.GrainId) });
            }
            else
            {
                var implicitConsumers = this.implicitTable.GetImplicitSubscribers(streamId, grainFactory);
                var subscriptions = implicitConsumers.Select(consumer =>
                {
                    var grainRef = consumer.Value as GrainReference;
                    var subId = consumer.Key;
                    return new StreamSubscription(subId, streamId.ProviderName, streamId, grainRef.GrainId);
                }).ToList();
                return Task.FromResult(subscriptions);
            }   
        }

        internal bool IsImplicitSubscriber(IAddressable addressable, InternalStreamId streamId)
        {
            return implicitTable.IsImplicitSubscriber(addressable.GetGrainId(), streamId);
        }

        internal bool IsImplicitSubscriber(GuidId subscriptionId, InternalStreamId streamId)
        {
            return SubscriptionMarker.IsImplicitSubscription(subscriptionId.Guid);
        }

        public GuidId CreateSubscriptionId(InternalStreamId streamId, IStreamConsumerExtension streamConsumer)
        {
            GrainId grainId = streamConsumer.GetGrainId();
            Guid subscriptionGuid;
            if (!implicitTable.TryGetImplicitSubscriptionGuid(grainId, streamId, out subscriptionGuid))
            {
                throw new ArgumentOutOfRangeException(streamId.ToString(), "Only implicit subscriptions are supported.");
            }
            return GuidId.GetGuidId(subscriptionGuid);
        }

        public Task<bool> FaultSubscription(InternalStreamId streamId, GuidId subscriptionId)
        {
            return Task.FromResult(false);
        }
    }
}
