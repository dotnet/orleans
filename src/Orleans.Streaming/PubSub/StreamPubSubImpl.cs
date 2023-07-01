using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Streams.Core;

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
                throw new ArgumentNullException(nameof(explicitPubSub));
            }

            if (implicitPubSub == null)
            {
                throw new ArgumentNullException(nameof(implicitPubSub));
            }

            this.explicitPubSub = explicitPubSub;
            this.implicitPubSub = implicitPubSub;
        }

        public async Task<ISet<PubSubSubscriptionState>> RegisterProducer(QualifiedStreamId streamId, GrainId streamProducer)
        {
            ISet<PubSubSubscriptionState> explicitRes = await explicitPubSub.RegisterProducer(streamId, streamProducer);
            ISet<PubSubSubscriptionState> implicitRes = await implicitPubSub.RegisterProducer(streamId, streamProducer);
            explicitRes.UnionWith(implicitRes);
            return explicitRes;
        }

        public Task UnregisterProducer(QualifiedStreamId streamId, GrainId streamProducer)
        {
            return explicitPubSub.UnregisterProducer(streamId, streamProducer);
        }

        public Task RegisterConsumer(GuidId subscriptionId, QualifiedStreamId streamId, GrainId streamConsumer, string filterData)
        {
            return implicitPubSub.IsImplicitSubscriber(streamConsumer, streamId)
                ? implicitPubSub.RegisterConsumer(subscriptionId, streamId, streamConsumer, filterData)
                : explicitPubSub.RegisterConsumer(subscriptionId, streamId, streamConsumer, filterData);
        }

        public Task UnregisterConsumer(GuidId subscriptionId, QualifiedStreamId streamId)
        {
            return implicitPubSub.IsImplicitSubscriber(subscriptionId, streamId)
                ? implicitPubSub.UnregisterConsumer(subscriptionId, streamId)
                : explicitPubSub.UnregisterConsumer(subscriptionId, streamId);
        }

        public Task<int> ProducerCount(QualifiedStreamId streamId)
        {
            return explicitPubSub.ProducerCount(streamId); 
        }

        public Task<int> ConsumerCount(QualifiedStreamId streamId)
        {
            return explicitPubSub.ConsumerCount(streamId); 
        }

        public async Task<List<StreamSubscription>> GetAllSubscriptions(QualifiedStreamId streamId, GrainId streamConsumer)
        {
            if (streamConsumer != default)
            {
                return implicitPubSub.IsImplicitSubscriber(streamConsumer, streamId)
                    ? await implicitPubSub.GetAllSubscriptions(streamId, streamConsumer)
                    : await explicitPubSub.GetAllSubscriptions(streamId, streamConsumer);
            }
            else
            {
                var implicitSubs = await implicitPubSub.GetAllSubscriptions(streamId);
                var explicitSubs = await explicitPubSub.GetAllSubscriptions(streamId);
                return implicitSubs.Concat(explicitSubs).ToList();
            }
        }

        public GuidId CreateSubscriptionId(QualifiedStreamId streamId, GrainId streamConsumer)
        {
            return implicitPubSub.IsImplicitSubscriber(streamConsumer, streamId)
               ? implicitPubSub.CreateSubscriptionId(streamId, streamConsumer)
               : explicitPubSub.CreateSubscriptionId(streamId, streamConsumer);
        }

        public Task<bool> FaultSubscription(QualifiedStreamId streamId, GuidId subscriptionId)
        {
            return implicitPubSub.IsImplicitSubscriber(subscriptionId, streamId)
                ? implicitPubSub.FaultSubscription(streamId, subscriptionId)
                : explicitPubSub.FaultSubscription(streamId, subscriptionId);
        }
    }
}
