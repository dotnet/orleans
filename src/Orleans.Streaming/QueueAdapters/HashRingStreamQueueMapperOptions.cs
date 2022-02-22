using Orleans.Streams;

namespace Orleans.Configuration
{
    /// <summary>
    /// Options for <see cref="HashRingBasedStreamQueueMapper"/>.
    /// </summary>
    public class HashRingStreamQueueMapperOptions
    {
        /// <summary>
        /// Gets or sets the total queue count.
        /// </summary>
        /// <value>The total queue count.</value>
        public int TotalQueueCount { get; set; } = DEFAULT_NUM_QUEUES;

        /// <summary>
        /// The default number queues, which should be a power of two.
        /// </summary>
        public const int DEFAULT_NUM_QUEUES = 8;
    }
}
