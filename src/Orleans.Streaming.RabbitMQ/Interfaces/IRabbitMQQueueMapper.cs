using Orleans.Streams;

namespace Orleans.RabbitMQ.Providers
{
    /// <summary>
    /// Stream queue mapper that maps RabbitMQ topic hashes to <see cref="QueueId"/>
    /// </summary>
    public interface IRabbitMQQueueMapper : IStreamQueueMapper
    {
        /// <summary>
        /// Gets the RabbitMQ topic hash by QueueId
        /// </summary>
        /// <param name="queueId"></param>
        /// <returns></returns>
        string QueueToPartition(QueueId queueId);
    }
}
