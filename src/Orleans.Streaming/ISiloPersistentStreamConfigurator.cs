using System;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Runtime.Providers;
using Orleans.Streams;

namespace Orleans.Hosting
{
    /// <summary>
    /// Functionality for configuring persistent streams.
    /// </summary>
    public interface ISiloPersistentStreamConfigurator : IPersistentStreamConfigurator { }

    /// <summary>
    /// Extensions for <see cref="ISiloPersistentStreamConfigurator"/>.
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
        /// Configures the pulling agents' message delivery backoff provider.
        /// </summary>
        /// <param name="configurator">The configuration builder.</param>
        /// <param name="factory">The message delivery backoff factory.</param>
        public static void ConfigureBackoffProvider(this ISiloPersistentStreamConfigurator configurator, Func<IServiceProvider, string, IMessageDeliveryBackoffProvider> factory)
        {
            configurator.ConfigureComponent(factory);
        }

        /// <summary>
        /// Configures the pulling agents' queue reader backoff provider.
        /// </summary>
        /// <param name="configurator">The configuration builder.</param>
        /// <param name="factory">The queue reader backoff factory.</param>
        public static void ConfigureBackoffProvider(this ISiloPersistentStreamConfigurator configurator, Func<IServiceProvider, string, IQueueReaderBackoffProvider> factory)
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
