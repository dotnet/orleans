using Microsoft.Extensions.DependencyInjection;
using Orleans.Clustering.DynamoDB;
using Orleans.Configuration;
using Orleans.Messaging;
using System;
using Microsoft.Extensions.Options;

namespace Orleans.Hosting
{
    public static class AwsUtilsHostingExtensions
    {
        /// <summary>
        /// Configures the silo to use DynamoDB for clustering.
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
        public static ISiloBuilder UseDynamoDBClustering(
            this ISiloBuilder builder,
            Action<DynamoDBClusteringOptions> configureOptions)
        {
            return builder.ConfigureServices(
                services =>
                {
                    if (configureOptions != null)
                    {
                        services.Configure(configureOptions);
                    }

                    services.AddSingleton<IMembershipTable, DynamoDBMembershipTable>();
                });
        }

        /// <summary>
        /// Configures the silo to use DynamoDB for clustering.
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
        public static ISiloBuilder UseDynamoDBClustering(
            this ISiloBuilder builder,
            Action<OptionsBuilder<DynamoDBClusteringOptions>> configureOptions)
        {
            return builder.ConfigureServices(
                services =>
                {
                    configureOptions?.Invoke(services.AddOptions<DynamoDBClusteringOptions>());
                    services.AddSingleton<IMembershipTable, DynamoDBMembershipTable>();
                });
        }

        /// <summary>
        /// Configures the client to use DynamoDB for clustering.
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
        public static IClientBuilder UseDynamoDBClustering(
            this IClientBuilder builder,
            Action<DynamoDBGatewayOptions> configureOptions)
        {
            return builder.ConfigureServices(
                services =>
                {
                    if (configureOptions != null)
                    {
                        services.Configure(configureOptions);
                    }

                    services.AddSingleton<IGatewayListProvider, DynamoDBGatewayListProvider>();
                });
        }

        /// <summary>
        /// Configures the client to use DynamoDB for clustering.
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
        public static IClientBuilder UseDynamoDBClustering(
            this IClientBuilder builder,
            Action<OptionsBuilder<DynamoDBGatewayOptions>> configureOptions)
        {
            return builder.ConfigureServices(
                services =>
                {
                    configureOptions?.Invoke(services.AddOptions<DynamoDBGatewayOptions>());
                    services.AddSingleton<IGatewayListProvider, DynamoDBGatewayListProvider>();
                });
        }
    }
}
