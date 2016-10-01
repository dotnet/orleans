namespace Orleans.Streams
{
    public enum StreamQueueBalancerType
    {
        /// <summary>
        /// Stream queue balancer that uses consistent ring provider for load balancing
        /// </summary>
        ConsistentRingBalancer, 

        /// <summary>
        /// Stream queue balancer that uses Azure deployment information and silo statuses from Membership oracle for load balancing.  
        /// Requires silo running in Azure.
        /// This Balancer uses both the information about the full set of silos as reported by Azure role code and 
        /// the information from Membership oracle about currently active (alive) silos and rebalances queues from non active silos.
        /// </summary>
        DynamicAzureDeploymentBalancer,

        /// <summary>
        /// Stream queue balancer that uses Azure deployment information for load balancing. 
        /// Requires silo running in Azure.
        /// This Balancer uses both the information about the full set of silos as reported by Azure role code but 
        /// does NOT use the information from Membership oracle about currently alive silos. 
        /// That is, it does not rebalance queues based on dymanic changes in the cluster Membership.
        /// </summary>
        StaticAzureDeploymentBalancer, 

        /// <summary>
        /// Stream queue balancer that uses the cluster configuration to determine deployment information for load balancing.  
        /// It does not support dynamic changes to global (full) cluster configuration.
        /// This Balancer does use the information from Membership oracle about currently active (alive) silos 
        /// and rebalances queues from non active silos.
        /// </summary>
        DynamicClusterConfigDeploymentBalancer,

        /// <summary>
        /// Stream queue balancer that uses the cluster configuration to determine deployment information for load balancing.  
        /// It does not support dynamic changes to global (full) cluster configuration.
        /// This Balancer does NOT use the information from Membership oracle about currently active silos.
        /// That is, it does not rebalance queues based on dymanic changes in the cluster Membership.
        /// </summary>
        StaticClusterConfigDeploymentBalancer,
    }
}
