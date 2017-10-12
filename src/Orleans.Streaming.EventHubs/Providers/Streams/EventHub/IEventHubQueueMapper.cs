
using Orleans.Streams;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Stream queue mapper that maps Event Hub partitions to <see cref="QueueId"/>s
    /// </summary>
    public interface IEventHubQueueMapper : IStreamQueueMapper
    {
        /// <summary>
        /// Gets the EventHub partition by QueueId
        /// </summary>
        /// <param name="queue"></param>
        /// <returns></returns>
        string QueueToPartition(QueueId queue);
    }
}
