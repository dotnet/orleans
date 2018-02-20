
using System;

namespace Orleans.Configuration
{
    /// <summary>
    /// Azure queue stream provider options.
    /// </summary>
    public class AzureQueueStreamOptions : PersistentStreamOptions
    {
        [RedactConnectionString]
        public string ConnectionString { get; set; }

        public string ClusterId { get; set; }

        public TimeSpan? MessageVisibilityTimeout { get; set; }

        public int CacheSize { get; set; } = DEFAULT_CACHE_SIZE;
        public const int DEFAULT_CACHE_SIZE = 4096;

        public int NumQueues { get; set; } = DEFAULT_NUM_QUEUES;
        public const int DEFAULT_NUM_QUEUES = 8; // keep as power of 2.
    }
}
