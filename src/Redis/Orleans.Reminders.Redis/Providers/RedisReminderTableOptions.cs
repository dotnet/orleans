using System;
using System.Threading.Tasks;

using StackExchange.Redis;

namespace Orleans.Configuration
{
    /// <summary>
    /// Redis reminder options.
    /// </summary>
    public class RedisReminderTableOptions
    {

        /// <summary>
        /// The connection string.
        /// </summary>
        public string ConnectionString { get; set; } = "localhost:6379";

        /// <summary>
        /// The database number.
        /// </summary>
        public int? DatabaseNumber { get; set; }

        /// <summary>
        /// The delegate used to create a Redis connection multiplexer.
        /// </summary>
        public Func<RedisReminderTableOptions, Task<IConnectionMultiplexer>> CreateMultiplexer { get; set; } = DefaultCreateMultiplexer;

        /// <summary>
        /// The default multiplexer creation delegate.
        /// </summary>
        public static async Task<IConnectionMultiplexer> DefaultCreateMultiplexer(RedisReminderTableOptions options)
        {
            return await ConnectionMultiplexer.ConnectAsync(options.ConnectionString);
        }

    }
}
