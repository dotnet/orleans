using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extensions for <see cref="ISiloHostBuilder"/> instances.
    /// </summary>
    public static class SiloHostBuilderExtensions
    {
        /// <summary>
        /// Adds services to the container. This can be called multiple times and the results will be additive.
        /// </summary>
        /// <param name="hostBuilder">The <see cref="ISiloHostBuilder" /> to configure.</param>
        /// <param name="configureDelegate"></param>
        /// <returns>The same instance of the <see cref="ISiloHostBuilder"/> for chaining.</returns>
        public static ISiloHostBuilder ConfigureServices(this ISiloHostBuilder hostBuilder, Action<IServiceCollection> configureDelegate)
        {
            return hostBuilder.ConfigureServices((context, collection) => configureDelegate(collection));
        }

        /// <summary>
        /// Sets up the configuration for the remainder of the build process and application. This can be called multiple times and
        /// the results will be additive. The results will be available at <see cref="HostBuilderContext.Configuration"/> for
        /// subsequent operations, as well as in <see cref="ISiloHost.Services"/>.
        /// </summary>
        /// <param name="hostBuilder">The host builder to configure.</param>
        /// <param name="configureDelegate"></param>
        /// <returns>The same instance of the host builder for chaining.</returns>
        public static ISiloHostBuilder ConfigureAppConfiguration(this ISiloHostBuilder hostBuilder, Action<IConfigurationBuilder> configureDelegate)
        {
            return hostBuilder.ConfigureAppConfiguration((context, builder) => configureDelegate(builder));
        }

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
        public static ISiloHostBuilder ConfigureLogging(this ISiloHostBuilder builder, Action<HostBuilderContext, ILoggingBuilder> configureLogging)
        {
            return builder.ConfigureServices((context, collection) => collection.AddLogging(loggingBuilder => configureLogging(context, loggingBuilder)));
        }

        /// <summary>
        /// Adds a delegate for configuring the provided <see cref="ILoggingBuilder"/>. This may be called multiple times.
        /// </summary>
        /// <param name="builder">The <see cref="ISiloHostBuilder" /> to configure.</param>
        /// <param name="configureLogging">The delegate that configures the <see cref="ILoggingBuilder"/>.</param>
        /// <returns>The same instance of the <see cref="ISiloHostBuilder"/> for chaining.</returns>
        public static ISiloHostBuilder ConfigureLogging(this ISiloHostBuilder builder, Action<ILoggingBuilder> configureLogging)
        {
            return builder.ConfigureServices(collection => collection.AddLogging(configureLogging));
        }
    }
}