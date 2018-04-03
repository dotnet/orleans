
namespace Orleans.Configuration
{
    /// <summary>
    /// Setting class for EHGeneratorStreamProvider
    /// </summary>
    public class EventDataGeneratorStreamOptions 
    {
        /// <summary>
        /// Configure eventhub partition count wanted. EventDataGeneratorStreamProvider would generate the same set of partitions based on the count, when initializing.
        /// For example, if parition count set at 5, the generated partitions will be  partition-0, partition-1, partition-2, partition-3, partiton-4
        /// </summary>
        public int EventHubPartitionCount = DefaultEventHubPartitionCount;
        /// <summary>
        /// Default EventHubPartitionRangeStart
        /// </summary>
        public const int DefaultEventHubPartitionCount = 4;
    }
}