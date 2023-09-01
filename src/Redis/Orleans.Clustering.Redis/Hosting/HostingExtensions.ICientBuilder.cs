using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Clustering.Redis;
using StackExchange.Redis;

namespace Microsoft.Extensions.Hosting
{
    /// <summary>
    /// Hosting extensions for Redis clustering.
    /// </summary>
    public static class RedisClusteringIClientBuilderExtensions
    {
        /// <summary>
        /// Configures Redis as the clustering provider.
        /// </summary>
        public static IClientBuilder UseRedisClustering(this IClientBuilder builder, Action<RedisClusteringOptions> configuration)
        {
            return builder.ConfigureServices(services =>
            {
                if (configuration != null)
                {
                    services.Configure(configuration);
                }

                services
                    .AddRedisClustering()
                    .AddSingleton<IGatewayListProvider, RedisGatewayListProvider>();
            });
        }

        /// <summary>
        /// Configures Redis as the clustering provider.
        /// </summary>
        public static IClientBuilder UseRedisClustering(this IClientBuilder builder, string redisConnectionString)
        {
            return builder.ConfigureServices(services => services
                .Configure<RedisClusteringOptions>(opt =>
                {
                    opt.ConfigurationOptions = ConfigurationOptions.Parse(redisConnectionString);
                })
                .AddRedisClustering()
                .AddSingleton<IGatewayListProvider, RedisGatewayListProvider>());
        }

    }
}
