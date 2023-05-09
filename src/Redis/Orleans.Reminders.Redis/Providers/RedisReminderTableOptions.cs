using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Reminders.Redis;
using Orleans.Runtime;
using StackExchange.Redis;

namespace Orleans.Configuration
{
    /// <summary>
    /// Redis reminder options.
    /// </summary>
    public class RedisReminderTableOptions
    {
        /// <summary>
        /// Gets or sets the Redis client options.
        /// </summary>
        [RedactRedisConfigurationOptions]
        public ConfigurationOptions ConfigurationOptions { get; set; }

        /// <summary>
        /// The delegate used to create a Redis connection multiplexer.
        /// </summary>
        public Func<RedisReminderTableOptions, Task<IConnectionMultiplexer>> CreateMultiplexer { get; set; } = DefaultCreateMultiplexer;

        /// <summary>
        /// Entry expiry, null by default. A value should be set ONLY for ephemeral environments (like in tests).
        /// Setting a value different from null will cause reminder entries to be deleted after some period of time.
        /// </summary>
        public TimeSpan? EntryExpiry { get; set; } = null;

        /// <summary>
        /// The default multiplexer creation delegate.
        /// </summary>
        public static async Task<IConnectionMultiplexer> DefaultCreateMultiplexer(RedisReminderTableOptions options) => await ConnectionMultiplexer.ConnectAsync(options.ConfigurationOptions);
    }

    internal class RedactRedisConfigurationOptions : RedactAttribute
    {
        public override string Redact(object value) => value is ConfigurationOptions cfg ? cfg.ToString(includePassword: false) : base.Redact(value);
    }

    /// <summary>
    /// Configuration validator for <see cref="RedisReminderTableOptions"/>.
    /// </summary>
    public class RedisReminderTableOptionsValidator : IConfigurationValidator
    {
        private readonly RedisReminderTableOptions _options;

        public RedisReminderTableOptionsValidator(IOptions<RedisReminderTableOptions> options)
        {
            _options = options.Value;
        }

        public void ValidateConfiguration()
        {
            if (_options.ConfigurationOptions == null)
            {
                throw new OrleansConfigurationException($"Invalid configuration for {nameof(RedisReminderTable)}. {nameof(RedisReminderTableOptions)}.{nameof(_options.ConfigurationOptions)} is required.");
            }
        }
    }
}
