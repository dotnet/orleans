using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Orleans.Configuration;
using Orleans.Runtime.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans
{
    public static class ClientBuilderExtensions
    {
        /// <summary>
        /// Gets the client configuration.
        /// </summary>
        public static ClientConfiguration Configuration(this IClusterClient client)
        {
            return client.ServiceProvider.GetService<ClientConfiguration>();
        }

        /// <summary>
        /// Loads configuration from the standard client configuration locations.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <remarks>
        /// This method loads the first client configuration file it finds, searching predefined directories for predefined file names.
        /// The following file names are tried in order:
        /// <list type="number">
        ///     <item>ClientConfiguration.xml</item>
        ///     <item>OrleansClientConfiguration.xml</item>
        ///     <item>Client.config</item>
        ///     <item>Client.xml</item>
        /// </list>
        /// The following directories are searched in order:
        /// <list type="number">
        ///     <item>The directory of the executing assembly.</item>
        ///     <item>The approot directory.</item>
        ///     <item>The current working directory.</item>
        ///     <item>The parent of the current working directory.</item>
        /// </list>
        /// Each directory is searched for all configuration file names before proceeding to the next directory.
        /// </remarks>
        /// <returns>The builder.</returns>
        public static IClientBuilder LoadConfiguration(this IClientBuilder builder)
        {
            builder.UseConfiguration(ClientConfiguration.StandardLoad());
            return builder;
        }

        /// <summary>
        /// Loads configuration from the provided location.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="configurationFilePath"></param>
        /// <returns>The builder.</returns>
        public static IClientBuilder LoadConfiguration(this IClientBuilder builder, string configurationFilePath)
        {
            builder.LoadConfiguration(new FileInfo(configurationFilePath));
            return builder;
        }

        /// <summary>
        /// Loads configuration from the provided location.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="configurationFile"></param>
        /// <returns>The builder.</returns>
        public static IClientBuilder LoadConfiguration(this IClientBuilder builder, FileInfo configurationFile)
        {
            var config = ClientConfiguration.LoadFromFile(configurationFile.FullName);
            if (config == null)
            {
                throw new ArgumentException(
                    $"Error loading client configuration file {configurationFile.FullName}",
                    nameof(configurationFile));
            }

            builder.UseConfiguration(config);
            return builder;
        }

        /// <summary>
        /// Specified the configuration to use for this client.
        /// </summary>
        /// <param name="builder">The client builder.</param>
        /// <param name="configuration">The configuration.</param>
        /// <remarks>This method may only be called once per builder instance.</remarks>
        /// <returns>The builder.</returns>
        public static IClientBuilder UseConfiguration(this IClientBuilder builder, ClientConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            return builder.ConfigureServices(services =>
            {
                services.AddLegacyClientConfigurationSupport(configuration);
            });
        }
    }
}
