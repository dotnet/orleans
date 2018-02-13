using System;

using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Runtime.ReminderService;

namespace Orleans.Hosting
{
    /// <summary>
    /// Silo host builder extensions.
    /// </summary>
    public static class SiloHostBuilderReminderExtensions
    {
        /// <summary>
        /// Adds reminder storage using ADO.NET.
        /// </summary>
        /// <param name="builder">
        /// The builder.
        /// </param>
        /// <param name="configure">
        /// The delegate used to configure the reminder store.
        /// </param>
        /// <returns>
        /// The provided <see cref="ISiloHostBuilder"/>, for chaining.
        /// </returns>
        public static ISiloHostBuilder UseAdoNetReminderService(this ISiloHostBuilder builder, Action<AdoNetReminderTableOptions> configure)
        {
            builder.ConfigureServices(services => services.UseAdoNetReminderService(configure));
            return builder;
        }

        /// <summary>
        /// Adds reminder storage using ADO.NET.
        /// </summary>
        /// <param name="builder">
        /// The builder.
        /// </param>
        /// <param name="connectionString">
        /// The storage connection string.
        /// </param>
        /// <returns>
        /// The provided <see cref="ISiloHostBuilder"/>, for chaining.
        /// </returns>
        public static ISiloHostBuilder UseAdoNetReminderService(this ISiloHostBuilder builder, string connectionString)
        {
            builder.UseAdoNetReminderService(options => options.ConnectionString = connectionString);
            return builder;
        }

        /// <summary>
        /// Adds reminder storage using ADO.NET.
        /// </summary>
        /// <param name="services">
        /// The service collection.
        /// </param>
        /// <param name="configure">
        /// The delegate used to configure the reminder store.
        /// </param>
        /// <returns>
        /// The provided <see cref="ISiloHostBuilder"/>, for chaining.
        /// </returns>
        public static IServiceCollection UseAdoNetReminderService(this IServiceCollection services, Action<AdoNetReminderTableOptions> configure)
        {
            services.AddSingleton<IReminderTable, AdoNetReminderTable>();
            services.TryConfigureFormatter<AdoNetReminderTableOptions, AdoNetReminderTableOptionsFormatter>();
            services.Configure(configure);
            return services;
        }

        /// <summary>
        /// Adds reminder storage using ADO.NET.
        /// </summary>
        /// <param name="services">
        /// The service collection.
        /// </param>
        /// <param name="connectionString">
        /// The storage connection string.
        /// </param>
        /// <returns>
        /// The provided <see cref="ISiloHostBuilder"/>, for chaining.
        /// </returns>
        public static IServiceCollection UseAdoNetReminderService(this IServiceCollection services, string connectionString)
        {
            services.UseAdoNetReminderService(options => options.ConnectionString = connectionString);
            return services;
        }
    }
}