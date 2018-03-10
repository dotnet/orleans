using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Streams;
using System;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using System.Text;
using Orleans.Hosting;

namespace Orleans.Streams
{
    public static class SiloPersistentStreamConfiguratorExtension
    {
        public static ISiloPersistentStreamConfigurator UseConsistentRingQueueBalancer(this ISiloPersistentStreamConfigurator configurator)
        {
            return configurator.ConfigurePartitionBalancing(ConsistentRingQueueBalancer.Create);
        }

        public static ISiloPersistentStreamConfigurator UseStaticClusterConfigDeploymentBalancer(this ISiloPersistentStreamConfigurator configurator, 
            TimeSpan? siloMaturityPeriod = null)
        {
            return configurator.ConfigurePartitionBalancing<DeploymentBasedQueueBalancerOptions>(
                (s, n) => DeploymentBasedQueueBalancer.Create(s, n, s.GetService<IOptions<StaticClusterDeploymentOptions>>().Value),
                options => options.Configure(op =>
            {
                op.IsFixed = true;
                if (siloMaturityPeriod.HasValue)
                    op.SiloMaturityPeriod = siloMaturityPeriod.Value;
            }));
        }

        public static ISiloPersistentStreamConfigurator UseDynamicClusterConfigDeploymentBalancer(this ISiloPersistentStreamConfigurator configurator,
           TimeSpan? siloMaturityPeriod = null)
        {
            return configurator.ConfigurePartitionBalancing<DeploymentBasedQueueBalancerOptions>(
                (s, n) => DeploymentBasedQueueBalancer.Create(s, n, s.GetService<IOptions<StaticClusterDeploymentOptions>>().Value),
                options => options.Configure(op =>
                {
                    op.IsFixed = false;
                    if (siloMaturityPeriod.HasValue)
                        op.SiloMaturityPeriod = siloMaturityPeriod.Value;
                }));
        }

        public static ISiloPersistentStreamConfigurator UseClusterConfigDeploymentLeaseBasedBalancer(this ISiloPersistentStreamConfigurator configurator, 
            Action<OptionsBuilder<LeaseBasedQueueBalancerOptions>> configureOptions = null)
        {
            return configurator.ConfigurePartitionBalancing<LeaseBasedQueueBalancerOptions>((s, n) => LeaseBasedQueueBalancer.Create(s, n, s.GetService<IOptions<StaticClusterDeploymentOptions>>().Value),
                configureOptions);
        }
    }
}
