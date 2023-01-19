using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Reminders.Redis;

namespace Orleans.Hosting
{
    /// <summary>
    /// Silo host builder extensions.
    /// </summary>
    public static class SiloBuilderReminderExtensions
    {
        /// <summary>
        /// Adds reminder storage backed by Redis.
        /// </summary>
        /// <param name="builder">
        /// The builder.
        /// </param>
        /// <param name="configure">
        /// The delegate used to configure the reminder store.
        /// </param>
        /// <returns>
        /// The provided <see cref="ISiloBuilder"/>, for chaining.
        /// </returns>
        public static ISiloBuilder UseRedisReminderService(this ISiloBuilder builder, Action<RedisReminderTableOptions> configure)
        {
            builder.ConfigureServices(services => services.UseRedisReminderService(configure));
            return builder;
        }

        /// <summary>
        /// Adds reminder storage backed by Redis.
        /// </summary>
        /// <param name="services">
        /// The service collection.
        /// </param>
        /// <param name="configure">
        /// The delegate used to configure the reminder store.
        /// </param>
        /// <returns>
        /// The provided <see cref="IServiceCollection"/>, for chaining.
        /// </returns>
        public static IServiceCollection UseRedisReminderService(this IServiceCollection services, Action<RedisReminderTableOptions> configure)
        {
            services.AddSingleton<IReminderTable, RedisReminderTable>();
            services.Configure<RedisReminderTableOptions>(configure);
            services.AddSingleton<IConfigurationValidator, RedisReminderTableOptionsValidator>();
            services.ConfigureFormatter<RedisReminderTableOptions>();
            return services;
        }
    }
}
