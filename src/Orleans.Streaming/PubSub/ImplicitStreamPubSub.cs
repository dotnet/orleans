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

        public Task<ISet<PubSubSubscriptionState>> RegisterProducer(QualifiedStreamId streamId, GrainId streamProducer)
        {
            ISet<PubSubSubscriptionState> result = new HashSet<PubSubSubscriptionState>();
            if (!ImplicitStreamSubscriberTable.IsImplicitSubscribeEligibleNameSpace(streamId.GetNamespace())) return Task.FromResult(result);

            IDictionary<Guid, GrainId> implicitSubscriptions = implicitTable.GetImplicitSubscribers(streamId, this.grainFactory);
            foreach (var kvp in implicitSubscriptions)
            {
                GuidId subscriptionId = GuidId.GetGuidId(kvp.Key);
                result.Add(new PubSubSubscriptionState(subscriptionId, streamId, kvp.Value));
            }
            return Task.FromResult(result);
        }

        public Task UnregisterProducer(QualifiedStreamId streamId, GrainId streamProducer)
        {
            return Task.CompletedTask;
        }

        public Task RegisterConsumer(GuidId subscriptionId, QualifiedStreamId streamId, GrainId streamConsumer, string filterData)
        {
            // TODO BPETIT filter data?
            if (!IsImplicitSubscriber(streamConsumer, streamId))
            {
                throw new ArgumentOutOfRangeException(streamId.ToString(), "Only implicit subscriptions are supported.");
            }
            return Task.CompletedTask;
        }

        public Task UnregisterConsumer(GuidId subscriptionId, QualifiedStreamId streamId)
        {
            if (!IsImplicitSubscriber(subscriptionId, streamId))
            {
                throw new ArgumentOutOfRangeException(streamId.ToString(), "Only implicit subscriptions are supported.");
            }
            return Task.CompletedTask;
        }

        public Task<int> ProducerCount(QualifiedStreamId streamId)
        {
            return Task.FromResult(0);
        }

        public Task<int> ConsumerCount(QualifiedStreamId streamId)
        {
            return Task.FromResult(0);
        }

        public Task<List<StreamSubscription>> GetAllSubscriptions(QualifiedStreamId streamId, GrainId streamConsumer = default)
        {
            if (!ImplicitStreamSubscriberTable.IsImplicitSubscribeEligibleNameSpace(streamId.GetNamespace()))
                return Task.FromResult(new List<StreamSubscription>());

            if (streamConsumer != default)
            {
                var subscriptionId = CreateSubscriptionId(streamId, streamConsumer);
                return Task.FromResult(new List<StreamSubscription>
                { new StreamSubscription(subscriptionId.Guid, streamId.ProviderName, streamId, streamConsumer) });
            }
            else
            {
                var implicitConsumers = this.implicitTable.GetImplicitSubscribers(streamId, grainFactory);
                var subscriptions = implicitConsumers.Select(consumer =>
                {
                    var grainId = consumer.Value;
                    var subId = consumer.Key;
                    return new StreamSubscription(subId, streamId.ProviderName, streamId, grainId);
                }).ToList();
                return Task.FromResult(subscriptions);
            }   
        }

        internal bool IsImplicitSubscriber(GrainId grainId, QualifiedStreamId streamId)
        {
            return implicitTable.IsImplicitSubscriber(grainId, streamId);
        }

        internal bool IsImplicitSubscriber(GuidId subscriptionId, QualifiedStreamId streamId)
        {
            return SubscriptionMarker.IsImplicitSubscription(subscriptionId.Guid);
        }

        public GuidId CreateSubscriptionId(QualifiedStreamId streamId, GrainId grainId)
        {
            Guid subscriptionGuid;
            if (!implicitTable.TryGetImplicitSubscriptionGuid(grainId, streamId, out subscriptionGuid))
            {
                throw new ArgumentOutOfRangeException(streamId.ToString(), "Only implicit subscriptions are supported.");
            }
            return GuidId.GetGuidId(subscriptionGuid);
        }

        public Task<bool> FaultSubscription(QualifiedStreamId streamId, GuidId subscriptionId)
        {
            return Task.FromResult(false);
        }
    }
}
