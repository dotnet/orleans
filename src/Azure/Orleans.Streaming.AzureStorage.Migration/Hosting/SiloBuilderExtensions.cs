using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Configuration.Internal;
using Orleans.LeaseProviders;
using Orleans.Streams;

namespace Orleans.Hosting
{
    public static class SiloBuilderMigrationExtensions
    {
        /// <summary>
        /// Configure silo to use azure queue persistent migration streams.
        /// </summary>
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
