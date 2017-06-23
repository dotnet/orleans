namespace Orleans.Streams
{
    /// <summary>
    /// Built-in stream queue balancer type which is supported natively in orleans
    /// </summary>
    public static class BuiltInStreamQueueBalancerType
    {
        /// <summary>
        /// Stream queue balancer that uses consistent ring provider for load balancing
        /// </summary>
        public const string ConsistentRingBalancer = nameof(ConsistentRingBalancer);

        /// <summary>
        /// Stream queue balancer that uses Azure deployment information and silo statuses from Membership oracle for load balancing.  
        /// Requires silo running in Azure.
        /// This Balancer uses both the information about the full set of silos as reported by Azure role code and 
        /// the information from Membership oracle about currently active (alive) silos and rebalances queues from non active silos.
        /// </summary>
        public const string DynamicAzureDeploymentBalancer = nameof(DynamicAzureDeploymentBalancer);

        /// <summary>
        /// Stream queue balancer that uses Azure deployment information for load balancing. 
        /// Requires silo running in Azure.
        /// This Balancer uses both the information about the full set of silos as reported by Azure role code but 
        /// does NOT use the information from Membership oracle about currently alive silos. 
        /// That is, it does not rebalance queues based on dymanic changes in the cluster Membership.
        /// </summary>
        public const string StaticAzureDeploymentBalancer = nameof(StaticAzureDeploymentBalancer);

        /// <summary>
        /// Stream queue balancer that uses the cluster configuration to determine deployment information for load balancing.  
        /// It does not support dynamic changes to global (full) cluster configuration.
        /// This Balancer does use the information from Membership oracle about currently active (alive) silos 
        /// and rebalances queues from non active silos.
        /// </summary>
        public const string DynamicClusterConfigDeploymentBalancer = nameof(DynamicClusterConfigDeploymentBalancer);

        /// <summary>
        /// Stream queue balancer that uses the cluster configuration to determine deployment information for load balancing.  
        /// It does not support dynamic changes to global (full) cluster configuration.
        /// This Balancer does NOT use the information from Membership oracle about currently active silos.
        /// That is, it does not rebalance queues based on dymanic changes in the cluster Membership.
        /// </summary>
        public const string StaticClusterConfigDeploymentBalancer = nameof(StaticClusterConfigDeploymentBalancer);
    }
}
