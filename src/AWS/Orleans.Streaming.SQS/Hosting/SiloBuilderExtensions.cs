using System;
using Orleans.Configuration;

namespace Orleans.Hosting
{
    /// <summary>
    /// Provides extension methods for configuring Amazon SQS-backed persistent streams on Orleans silos.
    /// </summary>
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configures the silo to use an Amazon SQS-backed persistent stream provider.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="name">The name of the stream provider.</param>
        /// <param name="configureOptions">The delegate used to configure the SQS options.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddSqsStreams(this ISiloBuilder builder, string name, Action<SqsOptions> configureOptions)
        {
            builder.AddSqsStreams(name, b =>
                b.ConfigureSqs(ob => ob.Configure(configureOptions)));
            return builder;
        }

        /// <summary>
        /// Configures the silo to use an Amazon SQS-backed persistent stream provider.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="name">The name of the stream provider.</param>
        /// <param name="configure">The delegate used to configure the stream provider.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddSqsStreams(this ISiloBuilder builder, string name, Action<SiloSqsStreamConfigurator> configure)
        {
            var configurator = new SiloSqsStreamConfigurator(name,
                configureServicesDelegate => builder.ConfigureServices(configureServicesDelegate));
            configure?.Invoke(configurator);
            return builder;
        }
    }
}
