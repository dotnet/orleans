using System;

namespace Orleans.Streams.Azure
{
    public class StreamQueueBalancerType
    {
        /// <summary>
        /// Stream queue balancer that uses Azure deployment information and silo statuses from Membership oracle for load balancing.  
        /// Requires silo running in Azure.
        /// This Balancer uses both the information about the full set of silos as reported by Azure role code and 
        /// the information from Membership oracle about currently active (alive) silos and rebalances queues from non active silos.
        /// </summary>
        public static Type DynamicAzureDeploymentBalancer = typeof(DynamicAzureDeploymentBalancer);

        /// <summary>
        /// Stream queue balancer that uses Azure deployment information for load balancing. 
        /// Requires silo running in Azure.
        /// This Balancer uses both the information about the full set of silos as reported by Azure role code but 
        /// does NOT use the information from Membership oracle about currently alive silos. 
        /// That is, it does not rebalance queues based on dymanic changes in the cluster Membership.
        /// </summary>
        public static Type StaticAzureDeploymentBalancer = typeof(StaticAzureDeploymentBalancer);

        /// <summary>
        ///  Stream queue balancer that uses Azure deployment information for load balancing. 
        /// Requires silo running in Azure.
        /// This balancer supports queue balancing in cluster auto-scale scenario, unexpected server failure scenario, and try to support ideal distribution 
        /// </summary>
        public static Type AzureDeploymentLeaseBasedBalancer = typeof(AzureDeploymentLeaseBasedBalancer);
    }
}
