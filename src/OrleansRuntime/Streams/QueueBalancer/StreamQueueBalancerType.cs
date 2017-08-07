using System;

namespace Orleans.Streams
{
    /// <summary>
    /// Built-in stream queue balancer type which is supported natively in orleans
    /// </summary>
    public static class StreamQueueBalancerType
    {
        /// <summary>
        /// Stream queue balancer that uses consistent ring provider for load balancing
        /// </summary>
        public static Type ConsistentRingBalancer = typeof(ConsistentRingQueueBalancer);

        /// <summary>
        /// Stream queue balancer that uses the cluster configuration to determine deployment information for load balancing.  
        /// It does not support dynamic changes to global (full) cluster configuration.
        /// This Balancer does use the information from Membership oracle about currently active (alive) silos 
        /// and rebalances queues from non active silos.
        /// </summary>
        public static Type DynamicClusterConfigDeploymentBalancer = typeof(DynamicClusterConfigDeploymentBalancer);

        /// <summary>
        /// Stream queue balancer that uses the cluster configuration to determine deployment information for load balancing.  
        /// It does not support dynamic changes to global (full) cluster configuration.
        /// This Balancer does NOT use the information from Membership oracle about currently active silos.
        /// That is, it does not rebalance queues based on dymanic changes in the cluster Membership.
        /// </summary>
        public static Type StaticClusterConfigDeploymentBalancer = typeof(StaticClusterConfigDeploymentBalancer);

        /// <summary>
        /// Stream queue balancer that uses the cluster configuration to determine deployment information for load balancing.  
        /// This balancer supports queue balancing in cluster auto-scale scenario, unexpected server failure scenario, and try to support ideal distribution 
        /// </summary>
        public static Type ClusterConfigDeploymentLeaseBasedBalancer = typeof(ClusterConfigDeploymentLeaseBasedBalancer);
    }
}
