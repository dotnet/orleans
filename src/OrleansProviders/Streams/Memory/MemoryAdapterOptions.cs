
using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace Orleans.Configuration
{
    /// <summary>
    /// This configuration class is used to configure the MemoryStreamProvider.
    /// It tells the stream provider how many queues to create.
    /// </summary>
    public class MemoryStreamOptions : RecoverableStreamOptions
    {
        /// <summary>
        /// Actual total queue count.
        /// </summary>
        public int TotalQueueCount { get; set; } = DEFAULT_TOTAL_QUEUE_COUNT;
        /// <summary>
        /// Total queue count default value.
        /// </summary>
        public const int DEFAULT_TOTAL_QUEUE_COUNT = 4;
    }
}
