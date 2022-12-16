using StackExchange.Redis;
using System;
using System.Threading.Tasks;

namespace Orleans.Clustering.Redis
{
    /// <summary>
    /// Options for Redis clustering.
    /// </summary>
    public class RedisClusteringOptions
    {
        /// <summary>
        /// Specifies the database identi
        /// </summary>
        public int Database { get; set; }

        /// <summary>
        /// The connection string.
        /// </summary>
        public string ConnectionString { get; set; } = "localhost:6379";

        /// <summary>
        /// The delegate used to create a Redis connection multiplexer.
        /// </summary>
        public Func<RedisClusteringOptions, Task<IConnectionMultiplexer>> CreateMultiplexer { get; set; } = DefaultCreateMultiplexer;

        /// <summary>
        /// The default multiplexer creation delegate.
        /// </summary>
        public static async Task<IConnectionMultiplexer> DefaultCreateMultiplexer(RedisClusteringOptions options)
        {
            return await ConnectionMultiplexer.ConnectAsync(options.ConnectionString);
        }
    }
}