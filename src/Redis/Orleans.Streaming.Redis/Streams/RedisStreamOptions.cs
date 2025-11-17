using System;
using System.Text;
using System.Threading.Tasks;
using Orleans.Streams;
using StackExchange.Redis;

namespace Orleans.Configuration
{
    /// <summary>
    /// Options for Redis streaming.
    /// </summary>
    public class RedisStreamOptions
    {
        /// <summary>
        /// Gets or sets the Redis client configuration.
        /// </summary>
        [RedactRedisConfigurationOptions]
        public ConfigurationOptions ConfigurationOptions { get; set; } = default!;

        /// <summary>
        /// The delegate used to create a Redis connection multiplexer.
        /// </summary>
        public Func<RedisStreamOptions, Task<IConnectionMultiplexer>> CreateMultiplexer { get; set; } = DefaultCreateMultiplexer;

        /// <summary>
        /// Gets the Redis key for the provided QueueId. If not set, the default implementation will be used, which is equivalent to <c>{ServiceId}/streams/{queueId}</c>.
        /// </summary>
        public Func<ClusterOptions, QueueId, RedisKey> GetRedisKey { get; set; } = DefaultGetRedisKey;

        /// <summary>
        /// The maximum length of the stream used to trim old entries.    
        /// </summary>    
        /// <remarks><seealso href="https://redis.io/topics/streams-intro"/></remarks>    
        public int MaxStreamLength { get; set; } = 1000;

        /// <summary>
        /// The time in minutes after which entries in the stream will be trimmed.
        /// </summary>
        public int TrimTimeMinutes { get; set; } = 5;


        /// <summary>
        /// The default multiplexer creation delegate.
        /// </summary>
        public static async Task<IConnectionMultiplexer> DefaultCreateMultiplexer(RedisStreamOptions options)
        {
            return await ConnectionMultiplexer.ConnectAsync(options.ConfigurationOptions);
        }

        /// <summary>
        /// The default redis key delegate.
        /// </summary>        
        private static RedisKey DefaultGetRedisKey(ClusterOptions clusterOptions, QueueId queueId)
        {
            RedisKey key = Encoding.UTF8.GetBytes($"{clusterOptions.ServiceId}/streams/{queueId}");
            return key;
        }
    }

    internal class RedactRedisConfigurationOptions : RedactAttribute
    {
        public override string Redact(object value) => value is ConfigurationOptions cfg ? cfg.ToString(includePassword: false) : base.Redact(value);
    }
}
