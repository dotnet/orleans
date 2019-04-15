using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Orleans.Streams
{
    public static class SiloPersistentStreamConfiguratorExtension
    {
        public static TConfigurator UseConsistentRingQueueBalancer<TConfigurator>(this TConfigurator configurator)
            where TConfigurator : NamedServiceConfigurator, ISiloPersistentStreamConfigurator
        {
            return configurator.ConfigurePartitionBalancing(ConsistentRingQueueBalancer.Create);
        }

        public static TConfigurator UseStaticClusterConfigDeploymentBalancer<TConfigurator>(this TConfigurator configurator, 
            TimeSpan? siloMaturityPeriod = null)
            where TConfigurator : NamedServiceConfigurator, ISiloPersistentStreamConfigurator
        {
            return configurator.ConfigurePartitionBalancing<TConfigurator,DeploymentBasedQueueBalancerOptions>(
                (s, n) => DeploymentBasedQueueBalancer.Create(s, n, s.GetService<IOptions<StaticClusterDeploymentOptions>>().Value),
                options => options.Configure(op =>
            {
                op.IsFixed = true;
                if (siloMaturityPeriod.HasValue)
                    op.SiloMaturityPeriod = siloMaturityPeriod.Value;
            }));
        }

        public static TConfigurator UseDynamicClusterConfigDeploymentBalancer<TConfigurator>(this TConfigurator configurator,
           TimeSpan? siloMaturityPeriod = null)
            where TConfigurator : NamedServiceConfigurator, ISiloPersistentStreamConfigurator
        {
            return configurator.ConfigurePartitionBalancing<TConfigurator,DeploymentBasedQueueBalancerOptions>(
                (s, n) => DeploymentBasedQueueBalancer.Create(s, n, s.GetService<IOptions<StaticClusterDeploymentOptions>>().Value),
                options => options.Configure(op =>
                {
                    op.IsFixed = false;
                    if (siloMaturityPeriod.HasValue)
                        op.SiloMaturityPeriod = siloMaturityPeriod.Value;
                }));
        }

        public static TConfigurator UseClusterConfigDeploymentLeaseBasedBalancer<TConfigurator>(this TConfigurator configurator, 
            Action<OptionsBuilder<LeaseBasedQueueBalancerOptions>> configureOptions = null)
            where TConfigurator : NamedServiceConfigurator, ISiloPersistentStreamConfigurator
        {
            return configurator.ConfigurePartitionBalancing((s, n) => LeaseBasedQueueBalancer.Create(s, n, s.GetService<IOptions<StaticClusterDeploymentOptions>>().Value),
                configureOptions);
        }
    }
}
