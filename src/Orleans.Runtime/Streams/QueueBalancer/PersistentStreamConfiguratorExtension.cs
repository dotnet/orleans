using Orleans.Configuration;
using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Streams
{
    public static class SiloPersistentStreamConfiguratorExtension
    {
        public static ISiloPersistentStreamConfigurator UseConsistentRingQueueBalancer(this ISiloPersistentStreamConfigurator configurator)
        {
            return configurator.ConfigureStreamQueueBalancer(ConsistentRingQueueBalancer.Create);
        }

        public static ISiloPersistentStreamConfigurator UseStaticClusterConfigDeploymentBalancer(this ISiloPersistentStreamConfigurator configurator, 
            TimeSpan? siloMaturityPeriod = null)
        {
            return configurator.ConfigureStreamQueueBalancer<DeploymentBasedQueueBalancerOptions>(options => options.Configure(op =>
            {
                op.IsFixed = true;
                if (siloMaturityPeriod.HasValue)
                    op.SiloMaturityPeriod = siloMaturityPeriod.Value;
            }), DeploymentBasedQueueBalancer.Create);
        }

        public static ISiloPersistentStreamConfigurator UseDynamicClusterConfigDeploymentBalancer(this ISiloPersistentStreamConfigurator configurator,
           TimeSpan? siloMaturityPeriod = null)
        {
            return configurator.ConfigureStreamQueueBalancer<DeploymentBasedQueueBalancerOptions>(options => options.Configure(op =>
            {
                op.IsFixed = false;
                if (siloMaturityPeriod.HasValue)
                    op.SiloMaturityPeriod = siloMaturityPeriod.Value;
            }), DeploymentBasedQueueBalancer.Create);
        }

        public static ISiloPersistentStreamConfigurator UseClusterConfigDeploymentLeaseBasedBalancer(this ISiloPersistentStreamConfigurator configurator, 
            Action<OptionsBuilder<LeaseBasedQueueBalancerOptions>> configureOptions = null)
        {
            return configurator.ConfigureStreamQueueBalancer<LeaseBasedQueueBalancerOptions>(configureOptions, LeaseBasedQueueBalancer.Create);
        }
    }
}
