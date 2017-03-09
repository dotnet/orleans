using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Streams
{
    internal interface IStreamPubSub // Compare with: IPubSubRendezvousGrain
    {
        Task<ISet<PubSubSubscriptionState>> RegisterProducer(StreamId streamId, string streamProvider, IStreamProducerExtension streamProducer);

        Task UnregisterProducer(StreamId streamId, string streamProvider, IStreamProducerExtension streamProducer);

        Task RegisterConsumer(GuidId subscriptionId, StreamId streamId, string streamProvider, IStreamConsumerExtension streamConsumer, IStreamFilterPredicateWrapper filter);

        Task UnregisterConsumer(GuidId subscriptionId, StreamId streamId, string streamProvider);

        Task<int> ProducerCount(Guid streamId, string streamProvider, string streamNamespace);

        Task<int> ConsumerCount(Guid streamId, string streamProvider, string streamNamespace);

        Task<List<GuidId>> GetAllSubscriptions(StreamId streamId, IStreamConsumerExtension streamConsumer);

        GuidId CreateSubscriptionId(StreamId streamId, IStreamConsumerExtension streamConsumer);

        Task<bool> FaultSubscription(StreamId streamId, GuidId subscriptionId);
    }
}