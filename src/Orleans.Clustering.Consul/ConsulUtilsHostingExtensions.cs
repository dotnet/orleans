using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Messaging;
using Orleans.Runtime.Membership;
using Orleans.Configuration;

namespace Orleans.Hosting
{
    public static class ConsulUtilsHostingExtensions
    {
        /// <summary>
        /// Configures the silo to use Consul for clustering.
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
        public static ISiloBuilder UseConsulClustering(
            this ISiloBuilder builder,
            Action<ConsulClusteringSiloOptions> configureOptions)
        {
            return builder.ConfigureServices(
                services =>
                {
                    if (configureOptions != null)
                    {
                        services.Configure(configureOptions);
                    }

                    services.AddSingleton<IMembershipTable, ConsulBasedMembershipTable>();
                });
        }

        /// <summary>
        /// Configures the silo to use Consul for clustering.
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
        public static ISiloBuilder UseConsulClustering(
            this ISiloBuilder builder,
            Action<OptionsBuilder<ConsulClusteringSiloOptions>> configureOptions)
        {
            return builder.ConfigureServices(
                services =>
                {
                    configureOptions?.Invoke(services.AddOptions<ConsulClusteringSiloOptions>());
                    services.AddSingleton<IMembershipTable, ConsulBasedMembershipTable>();
                });
        }

        /// <summary>
        /// Configures the client to use Consul for clustering.
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
        public static IClientBuilder UseConsulClustering(
            this IClientBuilder builder,
            Action<ConsulClusteringClientOptions> configureOptions)
        {
            return builder.ConfigureServices(services =>
                {
                    if (configureOptions != null)
                    {
                        services.Configure(configureOptions);
                    }

                    services.AddSingleton<IGatewayListProvider, ConsulGatewayListProvider>();
                });
        }

        /// <summary>
        /// Configures the client to use Consul for clustering.
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
        public static IClientBuilder UseConsulClustering(
            this IClientBuilder builder,
            Action<OptionsBuilder<ConsulClusteringClientOptions>> configureOptions)
        {
            return builder.ConfigureServices(
                services =>
                {
                    configureOptions?.Invoke(services.AddOptions<ConsulClusteringClientOptions>());
                    services.AddSingleton<IGatewayListProvider, ConsulGatewayListProvider>();
                });
        }
    }
}