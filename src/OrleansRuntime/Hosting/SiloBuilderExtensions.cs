using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extensions for <see cref="ISiloBuilder"/> instances.
    /// </summary>
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Registers an action used to configure a particular type of options.
        /// </summary>
        /// <typeparam name="TOptions">The options type to be configured.</typeparam>
        /// <param name="builder">The silo builder.</param>
        /// <param name="configureOptions">The action used to configure the options.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder Configure<TOptions>(this ISiloBuilder builder, Action<TOptions> configureOptions) where TOptions : class
        {
            return builder.ConfigureServices(services => services.Configure(configureOptions));
        }

        /// <summary>
        /// Configures the name of this silo.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="siloName">The silo name.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder ConfigureSiloName(this ISiloBuilder builder, string siloName)
        {
            builder.Configure<SiloIdentityOptions>(options => options.SiloName = siloName);
            return builder;
        }

        /// <summary>
        /// Specifies the configuration to use for this silo.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="configuration">The configuration.</param>
        /// <remarks>This method may only be called once per builder instance.</remarks>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder UseConfiguration(this ISiloBuilder builder, ClusterConfiguration configuration)
        {
            return builder.ConfigureServices(services => services.AddSingleton(configuration));
        }

        /// <summary>
        /// Loads <see cref="ClusterConfiguration"/> using <see cref="ClusterConfiguration.StandardLoad"/>.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder LoadClusterConfiguration(this ISiloBuilder builder)
        {
            var configuration = new ClusterConfiguration();
            configuration.StandardLoad();
            return builder.UseConfiguration(configuration);
        }
        
        /// <summary>
        /// Configures a localhost silo.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="siloPort">The silo-to-silo communication port.</param>
        /// <param name="gatewayPort">The client-to-silo communication port.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder ConfigureLocalHostPrimarySilo(this ISiloBuilder builder, int siloPort = 22222, int gatewayPort = 40000)
        {
            builder.ConfigureSiloName(Silo.PrimarySiloName);
            return builder.UseConfiguration(ClusterConfiguration.LocalhostPrimarySilo(siloPort, gatewayPort));
        }

        /// <summary>
        /// Specifies how the <see cref="IServiceProvider"/> for this silo is configured. 
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="factory">The service provider configuration method.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder UseServiceProviderFactory<TContainerBuilder>(ISiloBuilder builder, IServiceProviderFactory<TContainerBuilder> factory)
        {
            return builder.UseServiceProviderFactory(services => factory.CreateServiceProvider(factory.CreateBuilder(services)));
        }

        /// <summary>
        /// Specifies how the <see cref="IServiceProvider"/> for this silo is configured. 
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="configureServiceProvider">The service provider configuration method.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder UseServiceProviderFactory(this ISiloBuilder builder, Func<IServiceCollection, IServiceProvider> configureServiceProvider)
        {
            if (configureServiceProvider == null) throw new ArgumentNullException(nameof(configureServiceProvider));
            return builder.UseServiceProviderFactory(new DelegateServiceProviderFactory(configureServiceProvider));
        }
    }
}