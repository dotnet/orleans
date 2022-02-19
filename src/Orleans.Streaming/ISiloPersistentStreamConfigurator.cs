using System;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Streams;

namespace Orleans.Hosting
{
    /// <summary>
    /// Functionality for configuring persistent streams.
    /// </summary>
    public interface ISiloPersistentStreamConfigurator : IPersistentStreamConfigurator { }

    /// <summary>
    /// Extnesions for <see cref="ISiloPersistentStreamConfigurator"/>.
    /// </summary>
    public static class SiloPersistentStreamConfiguratorExtensions
    {
        /// <summary>
        /// Configures the pulling agent.
        /// </summary>
        /// <param name="configurator">The configuration builder.</param>
        /// <param name="configureOptions">The configuration delegate.</param>
        public static void ConfigurePullingAgent(this ISiloPersistentStreamConfigurator configurator, Action<OptionsBuilder<StreamPullingAgentOptions>> configureOptions = null)
        {
            configurator.Configure(configureOptions);
        }

        /// <summary>
        /// Configures the lifecycle.
        /// </summary>
        /// <param name="configurator">The configuration builder.</param>
        /// <param name="configureOptions">The configuration delegate.</param>
        public static void ConfigureLifecycle(this ISiloPersistentStreamConfigurator configurator, Action<OptionsBuilder<StreamLifecycleOptions>> configureOptions)
        {
            configurator.Configure(configureOptions);
        }

        /// <summary>
        /// Configures partition balancing.
        /// </summary>
        /// <param name="configurator">The configuration builder.</param>
        /// <param name="factory">The partition balancer factory.</param>
        public static void ConfigurePartitionBalancing(this ISiloPersistentStreamConfigurator configurator, Func<IServiceProvider, string, IStreamQueueBalancer> factory)
        {
            configurator.ConfigureComponent(factory);
        }

        /// <summary>
        /// Configures partition balancing.
        /// </summary>
        /// <typeparam name="TOptions">The partition balancer options.</typeparam>
        /// <param name="configurator">The configuration builder.</param>
        /// <param name="factory">The partition balancer factory.</param>
        /// <param name="configureOptions">The configuration delegate.</param>
        public static void ConfigurePartitionBalancing<TOptions>(
            this ISiloPersistentStreamConfigurator configurator,
            Func<IServiceProvider, string, IStreamQueueBalancer> factory,
            Action<OptionsBuilder<TOptions>> configureOptions)
            where TOptions : class, new()
        {
            configurator.ConfigureComponent(factory, configureOptions);
        }
    }
}
