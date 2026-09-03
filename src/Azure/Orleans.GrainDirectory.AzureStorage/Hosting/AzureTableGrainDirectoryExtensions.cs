using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainDirectory;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extension methods for configuring an Azure Table Storage grain directory.
    /// </summary>
    public static class AzureTableGrainDirectorySiloBuilderExtensions
    {
        /// <summary>
        /// Configures Azure Table Storage as the default grain directory.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="configureOptions">The delegate used to configure the provider.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder UseAzureTableGrainDirectoryAsDefault(
            this ISiloBuilder builder,
            Action<AzureTableGrainDirectoryOptions> configureOptions)
        {
            return builder.UseAzureTableGrainDirectoryAsDefault(ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configures Azure Table Storage as the default grain directory.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="configureOptions">The delegate used to configure the provider options builder.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder UseAzureTableGrainDirectoryAsDefault(
            this ISiloBuilder builder,
            Action<OptionsBuilder<AzureTableGrainDirectoryOptions>> configureOptions)
        {
            return builder.ConfigureServices(services => services.AddAzureTableGrainDirectory(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY, configureOptions));
        }

        /// <summary>
        /// Adds a named Azure Table Storage grain directory.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="name">The name of the grain directory.</param>
        /// <param name="configureOptions">The delegate used to configure the provider.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddAzureTableGrainDirectory(
            this ISiloBuilder builder,
            string name,
            Action<AzureTableGrainDirectoryOptions> configureOptions)
        {
            return builder.AddAzureTableGrainDirectory(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Adds a named Azure Table Storage grain directory.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="name">The name of the grain directory.</param>
        /// <param name="configureOptions">The delegate used to configure the provider options builder.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddAzureTableGrainDirectory(
            this ISiloBuilder builder,
            string name,
            Action<OptionsBuilder<AzureTableGrainDirectoryOptions>> configureOptions)
        {
            return builder.ConfigureServices(services => services.AddAzureTableGrainDirectory(name, configureOptions));
        }
    }
}
