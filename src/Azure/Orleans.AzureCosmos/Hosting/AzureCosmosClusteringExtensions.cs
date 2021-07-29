using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.AzureCosmos;
using Orleans.Configuration;
using Orleans.Messaging;

namespace Orleans.Hosting
{
    public static class AzureCosmosClusteringExtensions
    {
        /// <summary>
        /// Configures the silo to use Azure Cosmos DB for clustering.
        /// </summary>
        public static ISiloBuilder UseAzureCosmosClustering(this ISiloBuilder builder, Action<AzureCosmosClusteringOptions> configure)
            => builder.ConfigureServices(services =>
            {
                services.Configure(configure);
                services.AddSingleton<IMembershipTable, AzureCosmosClusteringStorage>();
                services.ConfigureFormatter<AzureCosmosClusteringOptions>();
            });

        /// <summary>
        /// Configures the silo to use Azure Cosmos DB for clustering.
        /// </summary>
        public static ISiloBuilder UseAzureCosmosClustering(this ISiloBuilder builder, Action<OptionsBuilder<AzureCosmosClusteringOptions>> configure)
            => builder.ConfigureServices(services =>
            {
                configure(services.AddOptions<AzureCosmosClusteringOptions>());
                services.AddSingleton<IMembershipTable, AzureCosmosClusteringStorage>();
                services.ConfigureFormatter<AzureCosmosClusteringOptions>();
            });

        /// <summary>
        /// Configures the client to use Azure Cosmos DB for clustering.
        /// </summary>
        public static IClientBuilder UseAzureCosmosClustering(this IClientBuilder builder, Action<AzureCosmosClusteringOptions> configure)
            => builder.ConfigureServices(services =>
            {
                services.Configure(configure);
                services.AddSingleton<IGatewayListProvider, AzureCosmosGatewayStorage>();
                services.ConfigureFormatter<AzureCosmosClusteringOptions>();
            });

        /// <summary>
        /// Configures the client to use Azure Cosmos DB for clustering.
        /// </summary>
        public static IClientBuilder UseAzureCosmosClustering(this IClientBuilder builder, Action<OptionsBuilder<AzureCosmosClusteringOptions>> configure)
            => builder.ConfigureServices(services =>
            {
                configure(services.AddOptions<AzureCosmosClusteringOptions>());
                services.AddSingleton<IGatewayListProvider, AzureCosmosGatewayStorage>();
                services.ConfigureFormatter<AzureCosmosClusteringOptions>();
            });
    }
}
