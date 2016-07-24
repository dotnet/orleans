using Orleans.Streams;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Stream queue mapper that maps Event Hub partitions to <see cref="QueueId"/>s
    /// </summary>
    public interface IEventHubQueueMapper : IStreamQueueMapper
    {
        string QueueToPartition(QueueId queue);
    }
}
