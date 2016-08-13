using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Streams
{
    internal interface IPubSubRendezvousGrain : IGrainWithGuidCompoundKey
    {
        Task<ISet<PubSubSubscriptionState>> RegisterProducer(StreamId streamId, IStreamProducerExtension streamProducer);

        Task UnregisterProducer(StreamId streamId, IStreamProducerExtension streamProducer);

        Task RegisterConsumer(GuidId subscriptionId, StreamId streamId, IStreamConsumerExtension streamConsumer, IStreamFilterPredicateWrapper filter);

        Task UnregisterConsumer(GuidId subscriptionId, StreamId streamId);

        Task<int> ProducerCount(StreamId streamId);

        Task<int> ConsumerCount(StreamId streamId);

        Task<PubSubSubscriptionState[]> DiagGetConsumers(StreamId streamId);

        Task Validate();

        Task<List<GuidId>> GetAllSubscriptions(StreamId streamId, IStreamConsumerExtension streamConsumer);

        Task FaultSubscription(GuidId subscriptionId);
    }
}
