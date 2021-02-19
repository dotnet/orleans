using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.ApplicationParts;
using Orleans.Configuration;
using Orleans;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extensions for <see cref="ISiloBuilder"/> instances.
    /// </summary>
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Adds services to the container. This can be called multiple times and the results will be additive.
        /// </summary>
        /// <param name="builder">The <see cref="ISiloBuilder" /> to configure.</param>
        /// <param name="configureDelegate"></param>
        /// <returns>The same instance of the <see cref="ISiloBuilder"/> for chaining.</returns>
        public static ISiloBuilder ConfigureServices(this ISiloBuilder builder, Action<IServiceCollection> configureDelegate)
        {
            return builder.ConfigureServices((context, collection) => configureDelegate(collection));
        }

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
        /// Registers a configuration instance which <typeparamref name="TOptions"/> will bind against.
        /// </summary>
        /// <typeparam name="TOptions">The options type to be configured.</typeparam>
        /// <param name="builder">The silo builder.</param>
        /// <param name="configuration">The configuration.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder Configure<TOptions>(this ISiloBuilder builder, IConfiguration configuration) where TOptions : class
        {
            return builder.ConfigureServices(services => services.AddOptions<TOptions>().Bind(configuration));
        }

        /// <summary>
        /// Adds a delegate for configuring the provided <see cref="ILoggingBuilder"/>. This may be called multiple times.
        /// </summary>
        /// <param name="builder">The <see cref="ISiloBuilder" /> to configure.</param>
        /// <param name="configureLogging">The delegate that configures the <see cref="ILoggingBuilder"/>.</param>
        /// <returns>The same instance of the <see cref="ISiloBuilder"/> for chaining.</returns>
        public static ISiloBuilder ConfigureLogging(this ISiloBuilder builder, Action<Microsoft.Extensions.Hosting.HostBuilderContext, ILoggingBuilder> configureLogging)
        {
            return builder.ConfigureServices((context, collection) => collection.AddLogging(loggingBuilder => configureLogging(context, loggingBuilder)));
        }

        /// <summary>
        /// Adds a delegate for configuring the provided <see cref="ILoggingBuilder"/>. This may be called multiple times.
        /// </summary>
        /// <param name="builder">The <see cref="ISiloBuilder" /> to configure.</param>
        /// <param name="configureLogging">The delegate that configures the <see cref="ILoggingBuilder"/>.</param>
        /// <returns>The same instance of the <see cref="ISiloBuilder"/> for chaining.</returns>
        public static ISiloBuilder ConfigureLogging(this ISiloBuilder builder, Action<ILoggingBuilder> configureLogging)
        {
            return builder.ConfigureServices(collection => collection.AddLogging(configureLogging));
        }

        /// <summary>
        /// Configures the <see cref="ApplicationPartManager"/> using the given <see cref="Action{IApplicationPartBuilder}"/>.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="configure">The configuration delegate.</param>
        /// <returns>The builder.</returns>
        public static ISiloBuilder ConfigureApplicationParts(this ISiloBuilder builder, Action<IApplicationPartManager> configure)
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            if (configure == null)
            {
                throw new ArgumentNullException(nameof(configure));
            }

            builder.ConfigureServices(services => configure(services.GetApplicationPartManager()));
            return builder;
        }
    }
}