using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Streams;

namespace Orleans.Hosting
{
    public static class SiloPersistentStreamConfiguratorExtension
    {
        public static void UseConsistentRingQueueBalancer(this ISiloPersistentStreamConfigurator configurator)
        {
            configurator.ConfigurePartitionBalancing(ConsistentRingQueueBalancer.Create);
        }

        public static void UseStaticClusterConfigDeploymentBalancer(this ISiloPersistentStreamConfigurator configurator, 
            TimeSpan? siloMaturityPeriod = null)
        {
            configurator.ConfigurePartitionBalancing<DeploymentBasedQueueBalancerOptions>(
                (s, n) => DeploymentBasedQueueBalancer.Create(s, n, s.GetRequiredService<IOptions<StaticClusterDeploymentOptions>>().Value),
                options => options.Configure(op =>
                {
                    op.IsFixed = true;
                    if (siloMaturityPeriod.HasValue)
                        op.SiloMaturityPeriod = siloMaturityPeriod.Value;
                }));
        }

        public static void UseDynamicClusterConfigDeploymentBalancer(this ISiloPersistentStreamConfigurator configurator,
   TimeSpan? siloMaturityPeriod = null)
        {
            configurator.ConfigurePartitionBalancing<DeploymentBasedQueueBalancerOptions>(
                (s, n) => DeploymentBasedQueueBalancer.Create(s, n, s.GetRequiredService<IOptions<StaticClusterDeploymentOptions>>().Value),
                options => options.Configure(op =>
                {
                    op.IsFixed = false;
                    if (siloMaturityPeriod.HasValue)
                        op.SiloMaturityPeriod = siloMaturityPeriod.Value;
                }));
        }

        public static void UseLeaseBasedQueueBalancer(this ISiloPersistentStreamConfigurator configurator, 
            Action<OptionsBuilder<LeaseBasedQueueBalancerOptions>> configureOptions = null)
        {
            configurator.ConfigurePartitionBalancing((s, n) => LeaseBasedQueueBalancer.Create(s, n),
                configureOptions);
        }
    }
}
