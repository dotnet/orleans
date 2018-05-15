﻿using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.AzureUtils;
using Orleans.Messaging;
using Orleans.Runtime.MembershipService;
using Orleans.Configuration;

namespace Orleans.Hosting
{
    public static class AzureTableClusteringExtensions
    {
        /// <summary>
        /// Configures the silo to use Azure Storage for clustering.
        /// </summary>
        /// <param name="builder">
        /// The silo builder.
        /// </param>
        /// <param name="configureOptions">
        /// The configuration delegate.
        /// </param>
        /// <returns>
        /// The provided <see cref="ISiloHostBuilder"/>.
        /// </returns>
        public static ISiloHostBuilder UseAzureStorageClustering(
            this ISiloHostBuilder builder,
            Action<AzureStorageClusteringOptions> configureOptions)
        {
            return builder.ConfigureServices(
                services =>
                {
                    if (configureOptions != null)
                    {
                        services.Configure(configureOptions);
                    }

                    services.AddSingleton<IMembershipTable, AzureBasedMembershipTable>()
                    .ConfigureFormatter<AzureStorageClusteringOptions>();
                });
        }

        /// <summary>
        /// Configures the silo to use Azure Storage for clustering.
        /// </summary>
        /// <param name="builder">
        /// The silo builder.
        /// </param>
        /// <param name="configureOptions">
        /// The configuration delegate.
        /// </param>
        /// <returns>
        /// The provided <see cref="ISiloHostBuilder"/>.
        /// </returns>
        public static ISiloHostBuilder UseAzureStorageClustering(
            this ISiloHostBuilder builder,
            Action<OptionsBuilder<AzureStorageClusteringOptions>> configureOptions)
        {
            return builder.ConfigureServices(
                services =>
                {
                    configureOptions?.Invoke(services.AddOptions<AzureStorageClusteringOptions>());
                    services.AddSingleton<IMembershipTable, AzureBasedMembershipTable>()
                    .ConfigureFormatter<AzureStorageClusteringOptions>();
                });
        }

        /// <summary>
        /// Configures the client to use Azure Storage for clustering.
        /// </summary>
        /// <param name="builder">
        /// The client builder.
        /// </param>
        /// <param name="configureOptions">
        /// The configuration delegate.
        /// </param>
        /// <returns>
        /// The provided <see cref="IClientBuilder"/>.
        /// </returns>
        public static IClientBuilder UseAzureStorageClustering(
            this IClientBuilder builder,
            Action<AzureStorageGatewayOptions> configureOptions)
        {
            return builder.ConfigureServices(
                services =>
                {
                    if (configureOptions != null)
                    {
                        services.Configure(configureOptions);
                    }

                    services.AddSingleton<IGatewayListProvider, AzureGatewayListProvider>()
                    .ConfigureFormatter<AzureStorageGatewayOptions>();
                });
        }

        /// <summary>
        /// Configures the client to use Azure Storage for clustering.
        /// </summary>
        /// <param name="builder">
        /// The client builder.
        /// </param>
        /// <param name="configureOptions">
        /// The configuration delegate.
        /// </param>
        /// <returns>
        /// The provided <see cref="IClientBuilder"/>.
        /// </returns>
        public static IClientBuilder UseAzureStorageClustering(
            this IClientBuilder builder,
            Action<OptionsBuilder<AzureStorageGatewayOptions>> configureOptions)
        {
            return builder.ConfigureServices(
                services =>
                {
                    configureOptions?.Invoke(services.AddOptions<AzureStorageGatewayOptions>());
                    services.AddSingleton<IGatewayListProvider, AzureGatewayListProvider>()
                    .ConfigureFormatter<AzureStorageGatewayOptions>();
                });
        }
    }
}
