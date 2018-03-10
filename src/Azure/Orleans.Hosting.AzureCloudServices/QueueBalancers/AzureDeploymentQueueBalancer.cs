using Orleans.Runtime;
using Orleans.Runtime.Host;
using System;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Streams
{
    public static class SiloPersistentStreamConfiguratorExtension
    {
        /// <summary>
        /// Stream queue balancer that uses Azure deployment information and silo statuses from Membership oracle for load balancing.  
        /// Requires silo running in Azure.
        /// This Balancer uses both the information about the full set of silos as reported by Azure role code and 
        /// the information from Membership oracle about currently active (alive) silos and rebalances queues from non active silos.
        /// </summary>
        public static ISiloPersistentStreamConfigurator UseDynamicAzureDeploymentBalancer(this ISiloPersistentStreamConfigurator configurator, 
            TimeSpan? siloMaturityPeriod = null)
        {
            return configurator.ConfigurePartitionBalancing<DeploymentBasedQueueBalancerOptions>(
                (s, n) => DeploymentBasedQueueBalancer.Create(s, n, new ServiceRuntimeWrapper(s.GetService<ILoggerFactory>())),
                options => options.Configure(op =>
                {
                    op.IsFixed = false;
                    if (siloMaturityPeriod.HasValue)
                        op.SiloMaturityPeriod = siloMaturityPeriod.Value;
                }));
        }

        /// <summary>
        /// Stream queue balancer that uses Azure deployment information for load balancing. 
        /// Requires silo running in Azure.
        /// This Balancer uses both the information about the full set of silos as reported by Azure role code but 
        /// does NOT use the information from Membership oracle about currently alive silos. 
        /// That is, it does not rebalance queues based on dymanic changes in the cluster Membership.
        /// </summary>
        public static ISiloPersistentStreamConfigurator UseStaticAzureDeploymentBalancer(this ISiloPersistentStreamConfigurator configurator,
           TimeSpan? siloMaturityPeriod = null)
        {
            return configurator.ConfigurePartitionBalancing<DeploymentBasedQueueBalancerOptions>(
                (s, n) => DeploymentBasedQueueBalancer.Create(s, n, new ServiceRuntimeWrapper(s.GetService<ILoggerFactory>())),
                options => options.Configure(op =>
                {
                    op.IsFixed = true;
                    if (siloMaturityPeriod.HasValue)
                        op.SiloMaturityPeriod = siloMaturityPeriod.Value;
                }));
        }

        /// <summary>
        ///  Stream queue balancer that uses Azure deployment information for load balancing. 
        /// Requires silo running in Azure.
        /// This balancer supports queue balancing in cluster auto-scale scenario, unexpected server failure scenario, and try to support ideal distribution 
        /// </summary>
        public static ISiloPersistentStreamConfigurator UseAzureDeploymentLeaseBasedBalancer(this ISiloPersistentStreamConfigurator configurator,
           Action<OptionsBuilder<LeaseBasedQueueBalancerOptions>> configureOptions = null)
        {
            return configurator.ConfigurePartitionBalancing<LeaseBasedQueueBalancerOptions>((s,n)=>LeaseBasedQueueBalancer.Create(s,n, new ServiceRuntimeWrapper(s.GetService<ILoggerFactory>())), configureOptions);
        }
    }
}
