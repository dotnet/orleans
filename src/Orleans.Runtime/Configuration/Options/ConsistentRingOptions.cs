namespace Orleans.Configuration
{
    /// <summary>
    /// Configuration options for consistent hashing algorithm, used to balance resource allocations across the cluster.
    /// </summary>
    public class ConsistentRingOptions
    {
        /// <summary>
        /// Determines the number of registrations a silo maintains in a consistent hash ring.  This affects the probabilistic
        ///   balancing of resource allocations across the cluster.  More virtual buckets increase the probability of evenly balancing
        ///   while minimally increasing management cost. 
        /// </summary>
        public int NumVirtualBucketsConsistentRing { get; set; } = DEFAULT_NUM_VIRTUAL_RING_BUCKETS;
        public const int DEFAULT_NUM_VIRTUAL_RING_BUCKETS = 30;

        /// <summary>
        /// Enables/Disables the use of virtual buckets.
        /// </summary>
        public bool UseVirtualBucketsConsistentRing { get; set; } = DEFAULT_USE_VIRTUAL_RING_BUCKETS;
        public const bool DEFAULT_USE_VIRTUAL_RING_BUCKETS = true;
    }
}
