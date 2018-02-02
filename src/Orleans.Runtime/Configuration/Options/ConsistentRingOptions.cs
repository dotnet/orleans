
namespace Orleans.Hosting
{
    public class ConsistentRingOptions
    {
        public int NumVirtualBucketsConsistentRing { get; set; } = DEFAULT_NUM_VIRTUAL_RING_BUCKETS;
        public const int DEFAULT_NUM_VIRTUAL_RING_BUCKETS = 30;

        public bool UseVirtualBucketsConsistentRing { get; set; } = DEFAULT_USE_VIRTUAL_RING_BUCKETS;
        public const bool DEFAULT_USE_VIRTUAL_RING_BUCKETS = true;
    }
}
