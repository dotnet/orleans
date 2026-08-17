using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Configuration.Internal;
using Orleans.LeaseProviders;
using Orleans.Streams;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extension methods for configuring Azure Queue migration streams on an Orleans silo.
    /// </summary>
    public static class SiloBuilderMigrationExtensions
    {
        /// <summary>
        /// Configures a silo to use Azure Queue persistent streams for migration.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="name">The name of the stream provider.</param>
        /// <param name="configure">The delegate used to configure the stream provider.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddAzureQueueMigrationStreams(
            this ISiloBuilder builder,
            string name,
            Action<SiloAzureQueueMigrationStreamConfigurator> configure)
        {
            var configurator = new SiloAzureQueueMigrationStreamConfigurator(name,
                configureServicesDelegate => builder.ConfigureServices(configureServicesDelegate),
                configureAppPartsDelegate => builder.ConfigureApplicationParts(configureAppPartsDelegate));
            configure?.Invoke(configurator);
            return builder;
        }
    }
}
