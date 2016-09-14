
namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// EventHub settings inteface for a specific hub.
    /// </summary>
    public interface IEventHubSettings
    {
        /// <summary>
        /// EventHub connection string.
        /// </summary>
        string ConnectionString { get; }
        /// <summary>
        /// EventHub consumer group.
        /// </summary>
        string ConsumerGroup { get; }
        /// <summary>
        /// Hub Path.
        /// </summary>
        string Path { get; }
        /// <summary>
        /// Optional parameter which configures the EventHub reciever's prefetch count.
        /// </summary>
        int? PrefetchCount { get; }

        /// <summary>
        /// Indicates if stream provider should read all new data in partition, or from start of partition.
        /// True - read all new data added to partition.
        /// False - start reading from beginning of partition.
        /// Note: If checkpoints are used, stream provider will always begin reading from most recent checkpoint.
        /// </summary>
        bool StartFromNow { get; }
    }
}
