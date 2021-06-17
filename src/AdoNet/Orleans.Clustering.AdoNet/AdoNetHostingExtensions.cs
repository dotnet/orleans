using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
        /// Configures this silo to use ADO.NET for clustering. Instructions on configuring your database are available at <see href="http://aka.ms/orleans-sql-scripts"/>.
        /// </summary>
        /// <param name="builder">
        /// The builder.
        /// </param>
        /// <param name="configureOptions">
        /// The configuration delegate.
        /// </param>
        /// <returns>
        /// The provided <see cref="ISiloBuilder"/>.
        /// </returns>
        /// <remarks>
        /// Instructions on configuring your database are available at <see href="http://aka.ms/orleans-sql-scripts"/>.
        /// </remarks>
        public static ISiloBuilder UseAdoNetClustering(
            this ISiloBuilder builder,
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
        /// Configures this silo to use ADO.NET for clustering. Instructions on configuring your database are available at <see href="http://aka.ms/orleans-sql-scripts"/>.
        /// </summary>
        /// <param name="builder">
        /// The builder.
        /// </param>
        /// <param name="configureOptions">
        /// The configuration delegate.
        /// </param>
        /// <returns>
        /// The provided <see cref="ISiloBuilder"/>.
        /// </returns>
        /// <remarks>
        /// Instructions on configuring your database are available at <see href="http://aka.ms/orleans-sql-scripts"/>.
        /// </remarks>
        public static ISiloBuilder UseAdoNetClustering(
            this ISiloBuilder builder,
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
        /// Configures this client to use ADO.NET for clustering. Instructions on configuring your database are available at <see href="http://aka.ms/orleans-sql-scripts"/>.
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
        /// <remarks>
        /// Instructions on configuring your database are available at <see href="http://aka.ms/orleans-sql-scripts"/>.
        /// </remarks>
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
        /// Configures this client to use ADO.NET for clustering. Instructions on configuring your database are available at <see href="http://aka.ms/orleans-sql-scripts"/>.
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
        /// <remarks>
        /// Instructions on configuring your database are available at <see href="http://aka.ms/orleans-sql-scripts"/>.
        /// </remarks>
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
