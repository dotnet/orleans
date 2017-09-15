using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extensions for <see cref="ISiloHostBuilder"/> instances.
    /// </summary>
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Registers an action used to configure a particular type of options.
        /// </summary>
        /// <typeparam name="TOptions">The options type to be configured.</typeparam>
        /// <param name="builder">The host builder.</param>
        /// <param name="configureOptions">The action used to configure the options.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloHostBuilder Configure<TOptions>(this ISiloHostBuilder builder, Action<TOptions> configureOptions) where TOptions : class
        {
            return builder.ConfigureServices(services => services.Configure(configureOptions));
        }

        /// <summary>
        /// Configures the name of this silo.
        /// </summary>
        /// <param name="builder">The host builder.</param>
        /// <param name="siloName">The silo name.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloHostBuilder ConfigureSiloName(this ISiloHostBuilder builder, string siloName)
        {
            builder.Configure<SiloIdentityOptions>(options => options.SiloName = siloName);
            return builder;
        }

        /// <summary>
        /// Specifies the configuration to use for this silo.
        /// </summary>
        /// <param name="builder">The host builder.</param>
        /// <param name="configuration">The configuration.</param>
        /// <remarks>This method may only be called once per builder instance.</remarks>
        /// <returns>The silo builder.</returns>
        public static ISiloHostBuilder UseConfiguration(this ISiloHostBuilder builder, ClusterConfiguration configuration)
        {
            return builder.ConfigureServices(services => services.AddSingleton(configuration));
        }

        /// <summary>
        /// Loads <see cref="ClusterConfiguration"/> using <see cref="ClusterConfiguration.StandardLoad"/>.
        /// </summary>
        /// <param name="builder">The host builder.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloHostBuilder LoadClusterConfiguration(this ISiloHostBuilder builder)
        {
            var configuration = new ClusterConfiguration();
            configuration.StandardLoad();
            return builder.UseConfiguration(configuration);
        }
        
        /// <summary>
        /// Configures a localhost silo.
        /// </summary>
        /// <param name="builder">The host builder.</param>
        /// <param name="siloPort">The silo-to-silo communication port.</param>
        /// <param name="gatewayPort">The client-to-silo communication port.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloHostBuilder ConfigureLocalHostPrimarySilo(this ISiloHostBuilder builder, int siloPort = 22222, int gatewayPort = 40000)
        {
            builder.ConfigureSiloName(Silo.PrimarySiloName);
            return builder.UseConfiguration(ClusterConfiguration.LocalhostPrimarySilo(siloPort, gatewayPort));
        }

        /// <summary>
        /// Specifies how the <see cref="IServiceProvider"/> for this silo is configured. 
        /// </summary>
        /// <param name="builder">The host builder.</param>
        /// <param name="factory">The service provider configuration method.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloHostBuilder UseServiceProviderFactory<TContainerBuilder>(ISiloHostBuilder builder, IServiceProviderFactory<TContainerBuilder> factory)
        {
            return builder.UseServiceProviderFactory(services => factory.CreateServiceProvider(factory.CreateBuilder(services)));
        }

        /// <summary>
        /// Specifies how the <see cref="IServiceProvider"/> for this silo is configured. 
        /// </summary>
        /// <param name="builder">The host builder.</param>
        /// <param name="configureServiceProvider">The service provider configuration method.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloHostBuilder UseServiceProviderFactory(this ISiloHostBuilder builder, Func<IServiceCollection, IServiceProvider> configureServiceProvider)
        {
            if (configureServiceProvider == null) throw new ArgumentNullException(nameof(configureServiceProvider));
            return builder.UseServiceProviderFactory(new DelegateServiceProviderFactory(configureServiceProvider));
        }

        /// <summary>
        /// Adds a delegate for configuring the provided <see cref="ILoggingBuilder"/>. This may be called multiple times.
        /// </summary>
        /// <param name="builder">The <see cref="ISiloHostBuilder" /> to configure.</param>
        /// <param name="configureLogging">The delegate that configures the <see cref="ILoggingBuilder"/>.</param>
        /// <returns>The same instance of the <see cref="ISiloHostBuilder"/> for chaining.</returns>
        public static ISiloHostBuilder ConfigureLogging(this ISiloHostBuilder builder, Action<ILoggingBuilder> configureLogging)
        {
            return builder.ConfigureServices(collection => collection.AddLogging(loggingBuilder => configureLogging(loggingBuilder)));
        }
    }
}