using System;

using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.DurableReminders.Redis;

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
        public static ISiloBuilder UseRedisDurableReminderService(this ISiloBuilder builder, Action<RedisReminderTableOptions> configure)
        {
            builder.ConfigureServices(services => services.UseRedisDurableReminderService(configure));
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
        public static IServiceCollection UseRedisDurableReminderService(this IServiceCollection services, Action<RedisReminderTableOptions> configure)
        {
            services.AddDurableReminders();
            services.AddSingleton<Orleans.DurableReminders.IReminderTable, RedisReminderTable>();
            services.Configure<RedisReminderTableOptions>(configure);
            services.AddSingleton<IConfigurationValidator, RedisReminderTableOptionsValidator>();
            services.ConfigureFormatter<RedisReminderTableOptions>();
            return services;
        }
    }
}
