using System;
using Orleans;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Clustering.Redis;

namespace Microsoft.Extensions.Hosting
{
    /// <summary>
    /// Hosting extensions for the Redis clustering provider.
    /// </summary>
    public static class RedisClusteringISiloBuilderExtensions
    {
        /// <summary>
        /// Configures Redis as the clustering provider.
        /// </summary>
        public static ISiloBuilder UseRedisClustering(this ISiloBuilder builder, Action<RedisClusteringOptions> configuration)
        {
            return builder.ConfigureServices(services =>
            {
                if (configuration != null)
                {
                    services.Configure(configuration);
                }

                services.AddRedis();
            });
        }

        /// <summary>
        /// Configures Redis as the clustering provider.
        /// </summary>
        public static ISiloBuilder UseRedisClustering(this ISiloBuilder builder, string redisConnectionString, int db = 0)
        {
            return builder.ConfigureServices(services => services
                .Configure<RedisClusteringOptions>(options => { options.Database = db; options.ConnectionString = redisConnectionString; })
                .AddRedis());
        }

        internal static IServiceCollection AddRedis(this IServiceCollection services)
        {
            services.AddSingleton<RedisMembershipTable>();
            services.AddSingleton<IMembershipTable>(sp => sp.GetRequiredService<RedisMembershipTable>());
            return services;
        }
    }
}
