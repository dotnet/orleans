
namespace Orleans.Configuration
{
    public class SqsStreamOptions : PersistentStreamOptions
    {
        public string ClusterId { get; set; }

        [Redact]
        public string ConnectionString { get; set; }

        public int CacheSize { get; set; } = CacheSizeDefaultValue;
        public const int CacheSizeDefaultValue = 4096;

        public int NumQueues { get; set; } = NumQueuesDefaultValue;
        public const int NumQueuesDefaultValue = 8; // keep as power of 2.
    }
}
