using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Streams.Core;

namespace Orleans.Streams
{
    internal interface IPubSubRendezvousGrain : IGrainWithStringKey
    {
        Task<ISet<PubSubSubscriptionState>> RegisterProducer(QualifiedStreamId streamId, IStreamProducerExtension streamProducer);

        Task UnregisterProducer(QualifiedStreamId streamId, IStreamProducerExtension streamProducer);

        Task RegisterConsumer(GuidId subscriptionId, QualifiedStreamId streamId, IStreamConsumerExtension streamConsumer, string filterData);

        Task UnregisterConsumer(GuidId subscriptionId, QualifiedStreamId streamId);

        Task<int> ProducerCount(QualifiedStreamId streamId);

        Task<int> ConsumerCount(QualifiedStreamId streamId);

        Task<PubSubSubscriptionState[]> DiagGetConsumers(QualifiedStreamId streamId);

        Task Validate();

        Task<List<StreamSubscription>> GetAllSubscriptions(QualifiedStreamId streamId, IStreamConsumerExtension streamConsumer = null);

        Task FaultSubscription(GuidId subscriptionId);
    }
}
