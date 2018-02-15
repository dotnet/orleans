using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Messaging;
using Orleans.Runtime.Membership;
using Orleans.Runtime.MembershipService;
using Orleans.Configuration;

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
        /// The provided <see cref="ISiloHostBuilder"/>.
        /// </returns>
        public static ISiloHostBuilder UseDynamoDBClustering(
            this ISiloHostBuilder builder,
            Action<DynamoDBClusteringSiloOptions> configureOptions)
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
        /// The provided <see cref="ISiloHostBuilder"/>.
        /// </returns>
        public static ISiloHostBuilder UseDynamoDBClustering(
            this ISiloHostBuilder builder,
            Action<OptionsBuilder<DynamoDBClusteringSiloOptions>> configureOptions)
        {
            return builder.ConfigureServices(
                services =>
                {
                    configureOptions?.Invoke(services.AddOptions<DynamoDBClusteringSiloOptions>());
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
            Action<DynamoDBClusteringClientOptions> configureOptions)
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
            Action<OptionsBuilder<DynamoDBClusteringClientOptions>> configureOptions)
        {
            return builder.ConfigureServices(
                services =>
                {
                    configureOptions?.Invoke(services.AddOptions<DynamoDBClusteringClientOptions>());
                    services.AddSingleton<IGatewayListProvider, DynamoDBGatewayListProvider>();
                });
        }
    }
}
