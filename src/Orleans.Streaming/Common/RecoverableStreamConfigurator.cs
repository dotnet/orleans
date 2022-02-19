using System;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Streams;

namespace Orleans.Hosting
{
    /// <summary>
    /// Silo-specific configuration builder for recoverable streams.
    /// </summary>
    public interface ISiloRecoverableStreamConfigurator : ISiloPersistentStreamConfigurator {}

    /// <summary>
    /// Extension methods for <see cref="ISiloRecoverableStreamConfigurator"/>.
    /// </summary>
    public static class SiloRecoverableStreamConfiguratorExtensions
    {
        /// <summary>
        /// Configures statistics options for a reliable stream provider.
        /// </summary>
        /// <param name="configurator">The configuration builder.</param>
        /// <param name="configureOptions">The configuration delegate.</param>
        public static void ConfigureStatistics(this ISiloRecoverableStreamConfigurator configurator, Action<OptionsBuilder<StreamStatisticOptions>> configureOptions)
        {
            configurator.Configure(configureOptions);
        }

        /// <summary>
        /// Configures cache eviction options for a reliable stream provider.
        /// </summary>
        /// <param name="configurator">The configuration builder.</param>
        /// <param name="configureOptions">The configuration delegate.</param>
        public static void ConfigureCacheEviction(this ISiloRecoverableStreamConfigurator configurator, Action<OptionsBuilder<StreamCacheEvictionOptions>> configureOptions)
        {
            configurator.Configure(configureOptions);
        }
    }

    /// <summary>
    /// Configures reliable streams.
    /// </summary>
    public class SiloRecoverableStreamConfigurator : SiloPersistentStreamConfigurator, ISiloRecoverableStreamConfigurator
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SiloRecoverableStreamConfigurator"/> class.
        /// </summary>
        /// <param name="name">The stream provider name.</param>
        /// <param name="configureDelegate">The configuration delegate.</param>
        /// <param name="adapterFactory">The adapter factory.</param>
        public SiloRecoverableStreamConfigurator(
            string name,
            Action<Action<IServiceCollection>> configureDelegate,
            Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory)
            : base(name, configureDelegate, adapterFactory)
        {
            this.ConfigureDelegate(services => services
                .ConfigureNamedOptionForLogging<StreamStatisticOptions>(name)
                .ConfigureNamedOptionForLogging<StreamCacheEvictionOptions>(name));
        }
    }
}
