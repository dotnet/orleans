using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
            => await RegisterProducer(streamId, streamProducer, CancellationToken.None);

        public async Task<ISet<PubSubSubscriptionState>> RegisterProducer(QualifiedStreamId streamId, GrainId streamProducer, CancellationToken cancellationToken)
        {
            ISet<PubSubSubscriptionState> explicitRes = await explicitPubSub.RegisterProducer(streamId, streamProducer, cancellationToken);
            ISet<PubSubSubscriptionState> implicitRes = await implicitPubSub.RegisterProducer(streamId, streamProducer, cancellationToken);
            explicitRes.UnionWith(implicitRes);
            return explicitRes;
        }

        public Task UnregisterProducer(QualifiedStreamId streamId, GrainId streamProducer)
            => UnregisterProducer(streamId, streamProducer, CancellationToken.None);

        public Task UnregisterProducer(QualifiedStreamId streamId, GrainId streamProducer, CancellationToken cancellationToken)
        {
            return explicitPubSub.UnregisterProducer(streamId, streamProducer, cancellationToken);
        }

        public Task RegisterConsumer(GuidId subscriptionId, QualifiedStreamId streamId, GrainId streamConsumer, string? filterData)
            => RegisterConsumer(subscriptionId, streamId, streamConsumer, filterData, CancellationToken.None);

        public Task RegisterConsumer(GuidId subscriptionId, QualifiedStreamId streamId, GrainId streamConsumer, string? filterData, CancellationToken cancellationToken)
        {
            return implicitPubSub.IsImplicitSubscriber(streamConsumer, streamId)
                ? implicitPubSub.RegisterConsumer(subscriptionId, streamId, streamConsumer, filterData, cancellationToken)
                : explicitPubSub.RegisterConsumer(subscriptionId, streamId, streamConsumer, filterData, cancellationToken);
        }

        public Task UnregisterConsumer(GuidId subscriptionId, QualifiedStreamId streamId)
            => UnregisterConsumer(subscriptionId, streamId, CancellationToken.None);

        public Task UnregisterConsumer(GuidId subscriptionId, QualifiedStreamId streamId, CancellationToken cancellationToken)
        {
            return implicitPubSub.IsImplicitSubscriber(subscriptionId, streamId)
                ? implicitPubSub.UnregisterConsumer(subscriptionId, streamId, cancellationToken)
                : explicitPubSub.UnregisterConsumer(subscriptionId, streamId, cancellationToken);
        }

        public Task<int> ProducerCount(QualifiedStreamId streamId)
            => ProducerCount(streamId, CancellationToken.None);

        public Task<int> ProducerCount(QualifiedStreamId streamId, CancellationToken cancellationToken)
        {
            return explicitPubSub.ProducerCount(streamId, cancellationToken);
        }

        public Task<int> ConsumerCount(QualifiedStreamId streamId)
            => ConsumerCount(streamId, CancellationToken.None);

        public Task<int> ConsumerCount(QualifiedStreamId streamId, CancellationToken cancellationToken)
        {
            return explicitPubSub.ConsumerCount(streamId, cancellationToken);
        }

        public async Task<List<StreamSubscription>> GetAllSubscriptions(QualifiedStreamId streamId, GrainId streamConsumer)
            => await GetAllSubscriptions(streamId, streamConsumer, CancellationToken.None);

        public async Task<List<StreamSubscription>> GetAllSubscriptions(QualifiedStreamId streamId, GrainId streamConsumer, CancellationToken cancellationToken)
        {
            if (streamConsumer != default)
            {
                return implicitPubSub.IsImplicitSubscriber(streamConsumer, streamId)
                    ? await implicitPubSub.GetAllSubscriptions(streamId, streamConsumer, cancellationToken)
                    : await explicitPubSub.GetAllSubscriptions(streamId, streamConsumer, cancellationToken);
            }
            else
            {
                var implicitSubs = await implicitPubSub.GetAllSubscriptions(streamId, default, cancellationToken);
                var explicitSubs = await explicitPubSub.GetAllSubscriptions(streamId, default, cancellationToken);
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
            => FaultSubscription(streamId, subscriptionId, CancellationToken.None);

        public Task<bool> FaultSubscription(QualifiedStreamId streamId, GuidId subscriptionId, CancellationToken cancellationToken)
        {
            return implicitPubSub.IsImplicitSubscriber(subscriptionId, streamId)
                ? implicitPubSub.FaultSubscription(streamId, subscriptionId, cancellationToken)
                : explicitPubSub.FaultSubscription(streamId, subscriptionId, cancellationToken);
        }
    }
}
