using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Storage;
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
            Action<OptionsBuilder<LeaseBasedQueueBalancerOptions>>? configureOptions = null)
        {
            configurator.ConfigureComponent((s, n) => LeaseBasedQueueBalancer.Create(s, n),
                configureOptions);
        }

        /// <summary>
        /// Configures the stream provider to use grain-based checkpointer.
        /// </summary>
        /// <remarks>
        /// Checkpoints are persisted using the <c>PubSubStore</c> grain storage provider.
        /// </remarks>
        /// <param name="configurator">The configuration builder.</param>
        public static void UseGrainCheckpointer(this ISiloPersistentStreamConfigurator configurator)
        {
            UseGrainCheckpointer(configurator, configureOptions: null);
        }

        /// <summary>
        /// Configures the stream provider to use a grain-based checkpointer.
        /// </summary>
        /// <remarks>
        /// The configured grain storage provider must be registered with the silo.
        /// Each stream provider can select a different grain storage provider.
        /// </remarks>
        /// <param name="configurator">The configuration builder.</param>
        /// <param name="configureOptions">The grain checkpointer configuration.</param>
        public static void UseGrainCheckpointer(
            this ISiloPersistentStreamConfigurator configurator,
            Action<OptionsBuilder<GrainStreamQueueCheckpointerOptions>>? configureOptions)
        {
            configurator.ConfigureComponent<GrainStreamQueueCheckpointerOptions, IStreamQueueCheckpointerFactory>(
                GrainStreamQueueCheckpointerFactory.CreateFactory,
                options =>
                {
                    options.Validate(
                        static value => value.PersistInterval > TimeSpan.Zero,
                        $"{nameof(GrainStreamQueueCheckpointerOptions.PersistInterval)} must be greater than zero.");
                    options.Validate(
                        static value => !string.IsNullOrWhiteSpace(value.StorageProviderName),
                        $"{nameof(GrainStreamQueueCheckpointerOptions.StorageProviderName)} is required.");
                    options.Validate<IServiceProviderIsKeyedService>(
                        static (value, services) => services.IsKeyedService(
                            typeof(IGrainStorage),
                            value.StorageProviderName),
                        $"{nameof(GrainStreamQueueCheckpointerOptions.StorageProviderName)} must identify a registered grain storage provider.");
                    options.ValidateOnStart();
                    configureOptions?.Invoke(options);
                });
        }
    }
}
