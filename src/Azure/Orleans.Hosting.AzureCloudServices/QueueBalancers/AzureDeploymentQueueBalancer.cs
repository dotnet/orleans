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
        public static ISiloPersistentStreamConfigurator UseDynamicAzureDeploymentBalancer(this ISiloPersistentStreamConfigurator configurator, 
            TimeSpan? siloMaturityPeriod = null)
        {
            return configurator.ConfigurePartitionBalancing<DeploymentBasedQueueBalancerOptions>(options => options.Configure(op =>
            {
                op.IsFixed = false;
                if (siloMaturityPeriod.HasValue)
                    op.SiloMaturityPeriod = siloMaturityPeriod.Value;
            }), (s,n) => DeploymentBasedQueueBalancer.Create(s,n,new ServiceRuntimeWrapper(s.GetService<ILoggerFactory>())));
        }

        public static ISiloPersistentStreamConfigurator UseStaticAzureDeploymentBalancer(this ISiloPersistentStreamConfigurator configurator,
           TimeSpan? siloMaturityPeriod = null)
        {
            return configurator.ConfigurePartitionBalancing<DeploymentBasedQueueBalancerOptions>(options => options.Configure(op =>
            {
                op.IsFixed = true;
                if (siloMaturityPeriod.HasValue)
                    op.SiloMaturityPeriod = siloMaturityPeriod.Value;
            }), (s, n) => DeploymentBasedQueueBalancer.Create(s, n, new ServiceRuntimeWrapper(s.GetService<ILoggerFactory>())));
        }

        /// <summary>
        ///  Stream queue balancer that uses Azure deployment information for load balancing. 
        /// Requires silo running in Azure.
        /// This balancer supports queue balancing in cluster auto-scale scenario, unexpected server failure scenario, and try to support ideal distribution 
        /// </summary>
        public static ISiloPersistentStreamConfigurator UseAzureDeploymentLeaseBasedBalancer(this ISiloPersistentStreamConfigurator configurator,
           Action<OptionsBuilder<LeaseBasedQueueBalancerOptions>> configureOptions = null)
        {
            return configurator.ConfigurePartitionBalancing<LeaseBasedQueueBalancerOptions>(configureOptions, (s,n)=>LeaseBasedQueueBalancer.Create(s,n, new ServiceRuntimeWrapper(s.GetService<ILoggerFactory>())));
        }
    }
}
