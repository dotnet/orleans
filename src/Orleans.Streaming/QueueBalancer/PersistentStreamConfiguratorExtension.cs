using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Streams;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extension methods for <see cref="ISiloPersistentStreamConfigurator"/>.
    /// </summary>
    public static class SiloPersistentStreamConfiguratorExtension
    {
        /// <summary>
        /// Configures the stream provider to use the consistent ring queue balancer.
        /// </summary>
        /// <param name="configurator">The confiurator.</param>
        public static void UseConsistentRingQueueBalancer(this ISiloPersistentStreamConfigurator configurator)
        {
            configurator.ConfigurePartitionBalancing(ConsistentRingQueueBalancer.Create);
        }

        /// <summary>
        /// Configures the stream provider to use the static cluster configuration deployment balancer.
        /// </summary>
        /// <param name="configurator">The configuration builder.</param>
        /// <param name="siloMaturityPeriod">The silo maturity period.</param>
        public static void UseStaticClusterConfigDeploymentBalancer(
            this ISiloPersistentStreamConfigurator configurator, 
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

        /// <summary>
        /// Configures the stream provider to use the dynamic cluster configuration deployment balancer.
        /// </summary>
        /// <param name="configurator">The configuration builder.</param>
        /// <param name="siloMaturityPeriod">The silo maturity period.</param>
        public static void UseDynamicClusterConfigDeploymentBalancer(
            this ISiloPersistentStreamConfigurator configurator,
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

        /// <summary>
        /// Configures the stream provider to use the lease based queue balancer.
        /// </summary>
        /// <param name="configurator">The configuration builder.</param>
        /// <param name="configureOptions">The configure options.</param>
        public static void UseLeaseBasedQueueBalancer(this ISiloPersistentStreamConfigurator configurator, 
            Action<OptionsBuilder<LeaseBasedQueueBalancerOptions>> configureOptions = null)
        {
            configurator.ConfigurePartitionBalancing((s, n) => LeaseBasedQueueBalancer.Create(s, n),
                configureOptions);
        }
    }
}
