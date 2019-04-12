using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Runtime.Host;
using Orleans.Configuration;

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
        public static TConfigurator UseDynamicAzureDeploymentBalancer<TConfigurator>(this TConfigurator configurator, 
            TimeSpan? siloMaturityPeriod = null)
            where TConfigurator : NamedServiceConfigurator, ISiloPersistentStreamConfigurator
        {
            return configurator.ConfigurePartitionBalancing<TConfigurator,DeploymentBasedQueueBalancerOptions>(
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
        /// That is, it does not rebalance queues based on dynamic changes in the cluster Membership.
        /// </summary>
        public static TConfigurator UseStaticAzureDeploymentBalancer<TConfigurator>(this TConfigurator configurator,
           TimeSpan? siloMaturityPeriod = null)
            where TConfigurator : NamedServiceConfigurator, ISiloPersistentStreamConfigurator
        {
            return configurator.ConfigurePartitionBalancing<TConfigurator,DeploymentBasedQueueBalancerOptions>(
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
        public static TConfigurator UseAzureDeploymentLeaseBasedBalancer<TConfigurator>(this TConfigurator configurator,
           Action<OptionsBuilder<LeaseBasedQueueBalancerOptions>> configureOptions = null)
            where TConfigurator : NamedServiceConfigurator, ISiloPersistentStreamConfigurator
        {
            return configurator.ConfigurePartitionBalancing(
                (s,n)=>LeaseBasedQueueBalancer.Create(s,n, new ServiceRuntimeWrapper(s.GetService<ILoggerFactory>())), configureOptions);
        }
    }
}
