
namespace Orleans.Configuration
{
    /// <summary>
    /// This configuration class is used to configure the GeneratorStreamProvider.
    /// It tells the stream provider how many queues to create, and which generator to use to generate event streams.
    /// </summary>
    public class GeneratedStreamOptions
    {
        /// <summary>
        /// Total number of queues
        /// </summary>
        public int TotalQueueCount { get; set; } = DEFAULT_TOTAL_QUEUE_COUNT;
        public const int DEFAULT_TOTAL_QUEUE_COUNT = 4;
    }
}
