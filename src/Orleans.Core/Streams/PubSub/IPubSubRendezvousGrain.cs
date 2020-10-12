using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Streams.Core;

namespace Orleans.Streams
{
    internal interface IPubSubRendezvousGrain : IGrainWithStringKey
    {
        Task<ISet<PubSubSubscriptionState>> RegisterProducer(InternalStreamId streamId, IStreamProducerExtension streamProducer);

        Task UnregisterProducer(InternalStreamId streamId, IStreamProducerExtension streamProducer);

        Task RegisterConsumer(GuidId subscriptionId, InternalStreamId streamId, IStreamConsumerExtension streamConsumer, string filterData);

        Task UnregisterConsumer(GuidId subscriptionId, InternalStreamId streamId);

        Task<int> ProducerCount(InternalStreamId streamId);

        Task<int> ConsumerCount(InternalStreamId streamId);

        Task<PubSubSubscriptionState[]> DiagGetConsumers(InternalStreamId streamId);

        Task Validate();

        Task<List<StreamSubscription>> GetAllSubscriptions(InternalStreamId streamId, IStreamConsumerExtension streamConsumer = null);

        Task FaultSubscription(GuidId subscriptionId);
    }
}
