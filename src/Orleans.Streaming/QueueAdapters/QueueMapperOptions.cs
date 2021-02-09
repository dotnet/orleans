namespace Orleans.Configuration
{
    public class HashRingStreamQueueMapperOptions
    {
        public int TotalQueueCount { get; set; } = DEFAULT_NUM_QUEUES;
        public const int DEFAULT_NUM_QUEUES = 8; // keep as power of 2.
    }
}
