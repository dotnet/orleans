using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using StackExchange.Redis;

namespace Orleans.AdvancedReminders.Redis
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
        public ConfigurationOptions ConfigurationOptions { get; set; } = new();

        /// <summary>
        /// The delegate used to create a Redis connection multiplexer and indicate whether it is shared.
        /// </summary>
        /// <remarks>
        /// When <c>IsShared</c> is <see langword="true"/>, the provider will not dispose the returned multiplexer.
        /// </remarks>
        public Func<RedisReminderTableOptions, Task<(IConnectionMultiplexer Multiplexer, bool IsShared)>> CreateMultiplexer { get; set; } = DefaultCreateMultiplexer;

        /// <summary>
        /// Table inactivity expiry, null by default. A value should be set ONLY for ephemeral environments (like in tests).
        /// All reminders share one Redis key, so every successful upsert refreshes this expiry for the entire table.
        /// If the table receives no successful upserts for the configured period, all reminders are deleted together.
        /// </summary>
        public TimeSpan? EntryExpiry { get; set; } = null;

        /// <summary>
        /// The default multiplexer creation delegate.
        /// </summary>
        public static async Task<(IConnectionMultiplexer Multiplexer, bool IsShared)> DefaultCreateMultiplexer(RedisReminderTableOptions options)
            => (Multiplexer: await ConnectionMultiplexer.ConnectAsync(options.ConfigurationOptions), IsShared: false);
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
            if (_options.CreateMultiplexer == null)
            {
                throw new OrleansConfigurationException($"Invalid configuration for {nameof(RedisReminderTable)}. {nameof(RedisReminderTableOptions)}.{nameof(_options.CreateMultiplexer)} is required.");
            }
            if (_options.EntryExpiry is { } expiry && expiry <= TimeSpan.Zero)
            {
                throw new OrleansConfigurationException($"Invalid configuration for {nameof(RedisReminderTable)}. {nameof(RedisReminderTableOptions)}.{nameof(_options.EntryExpiry)} must be greater than zero.");
            }
        }
    }
}
