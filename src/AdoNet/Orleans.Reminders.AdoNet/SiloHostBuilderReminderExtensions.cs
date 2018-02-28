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
        /// <param name="invariant">
        /// The ADO.NET invariant name.
        /// </param>
        /// <returns>
        /// The provided <see cref="ISiloHostBuilder"/>, for chaining.
        /// </returns>
        public static ISiloHostBuilder UseAdoNetReminderService(this ISiloHostBuilder builder, string connectionString, string invariant)
        {
            builder.UseAdoNetReminderService(options =>
            {
                options.ConnectionString = connectionString;
                options.Invariant = invariant;
            });
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
        private static void UseAdoNetReminderService(this IServiceCollection services, Action<AdoNetReminderTableOptions> configure)
        {
            services.AddSingleton<IReminderTable, AdoNetReminderTable>();
            services.ConfigureFormatter<AdoNetReminderTableOptions>();
            services.AddSingleton<IConfigurationValidator, AdoNetReminderTableOptionsValidator>();
            services.Configure(configure);
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
        /// <param name="invariant">
        /// The ADO.NET invariant name.
        /// </param>
        /// <returns>
        /// The provided <see cref="ISiloHostBuilder"/>, for chaining.
        /// </returns>
        internal static void UseAdoNetReminderService(this IServiceCollection services, string connectionString, string invariant)
        {
            services.UseAdoNetReminderService(options =>
            {
                options.ConnectionString = connectionString;
                options.Invariant = invariant;
            });
        }
    }
}