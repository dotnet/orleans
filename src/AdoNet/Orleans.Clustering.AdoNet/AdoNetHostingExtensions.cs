using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Messaging;
using Orleans.Runtime.Membership;
using Orleans.Runtime.MembershipService;
using Orleans.Configuration;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extensions for configuring ADO.NET for clustering.
    /// </summary>
    public static class AdoNetHostingExtensions
    {
        /// <summary>
        /// Configures this silo to use ADO.NET for clustering.
        /// </summary>
        /// <param name="builder">
        /// The builder.
        /// </param>
        /// <param name="configureOptions">
        /// The configuration delegate.
        /// </param>
        /// <returns>
        /// The provided <see cref="ISiloHostBuilder"/>.
        /// </returns>
        public static ISiloHostBuilder UseAdoNetClustering(
            this ISiloHostBuilder builder,
            Action<AdoNetClusteringSiloOptions> configureOptions)
        {
            return builder.ConfigureServices(
                services =>
                {
                    if (configureOptions != null)
                    {
                        services.Configure(configureOptions);
                    }

                    services.AddSingleton<IMembershipTable, AdoNetClusteringTable>();
                    services.AddSingleton<IConfigurationValidator, AdoNetClusteringSiloOptionsValidator>();
                });
        }

        /// <summary>
        /// Configures this silo to use ADO.NET for clustering.
        /// </summary>
        /// <param name="builder">
        /// The builder.
        /// </param>
        /// <param name="configureOptions">
        /// The configuration delegate.
        /// </param>
        /// <returns>
        /// The provided <see cref="ISiloHostBuilder"/>.
        /// </returns>
        public static ISiloHostBuilder UseAdoNetClustering(
            this ISiloHostBuilder builder,
            Action<OptionsBuilder<AdoNetClusteringSiloOptions>> configureOptions)
        {
            return builder.ConfigureServices(
                services =>
                {
                    configureOptions?.Invoke(services.AddOptions<AdoNetClusteringSiloOptions>());
                    services.AddSingleton<IMembershipTable, AdoNetClusteringTable>();
                    services.AddSingleton<IConfigurationValidator, AdoNetClusteringSiloOptionsValidator>();
                });
        }

        /// <summary>
        /// Configures this client to use ADO.NET for clustering.
        /// </summary>
        /// <param name="builder">
        /// The builder.
        /// </param>
        /// <param name="configureOptions">
        /// The configuration delegate.
        /// </param>
        /// <returns>
        /// The provided <see cref="IClientBuilder"/>.
        /// </returns>
        public static IClientBuilder UseAdoNetClustering(
            this IClientBuilder builder,
            Action<AdoNetClusteringClientOptions> configureOptions)
        {
            return builder.ConfigureServices(
                services =>
                {
                    if (configureOptions != null)
                    {
                        services.Configure(configureOptions);
                    }

                    services.AddSingleton<IGatewayListProvider, AdoNetGatewayListProvider>();
                    services.AddSingleton<IConfigurationValidator, AdoNetClusteringClientOptionsValidator>();
                });
        }

        /// <summary>
        /// Configures this client to use ADO.NET for clustering.
        /// </summary>
        /// <param name="builder">
        /// The builder.
        /// </param>
        /// <param name="configureOptions">
        /// The configuration delegate.
        /// </param>
        /// <returns>
        /// The provided <see cref="IClientBuilder"/>.
        /// </returns>
        public static IClientBuilder UseAdoNetClustering(
            this IClientBuilder builder,
            Action<OptionsBuilder<AdoNetClusteringClientOptions>> configureOptions)
        {
            return builder.ConfigureServices(
                services =>
                {
                    configureOptions?.Invoke(services.AddOptions<AdoNetClusteringClientOptions>());
                    services.AddSingleton<IGatewayListProvider, AdoNetGatewayListProvider>();
                    services.AddSingleton<IConfigurationValidator, AdoNetClusteringClientOptionsValidator>();
                });
        }
    }
}
