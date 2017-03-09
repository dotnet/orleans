using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Streams
{
    internal interface IStreamProducerExtension : IGrainExtension
    {
        [AlwaysInterleave]
        Task AddSubscriber(GuidId subscriptionId, StreamId streamId, IStreamConsumerExtension streamConsumer, IStreamFilterPredicateWrapper filter);

        [AlwaysInterleave]
        Task RemoveSubscriber(GuidId subscriptionId, StreamId streamId);
    }
}