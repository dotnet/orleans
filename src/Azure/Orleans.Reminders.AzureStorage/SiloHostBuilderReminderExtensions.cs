using System;

using Microsoft.Extensions.DependencyInjection;

using Orleans.Runtime.ReminderService;

namespace Orleans.Hosting
{
    /// <summary>
    /// Silo host builder extensions.
    /// </summary>
    public static class SiloHostBuilderReminderExtensions
    {
        /// <summary>
        /// Adds reminder storage backed by Azure Table Storage.
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
        public static ISiloHostBuilder UseAzureTableReminderService(this ISiloHostBuilder builder, Action<AzureTableReminderStorageOptions> configure)
        {
            builder.ConfigureServices(services => services.UseAzureTableReminderService(configure));
            return builder;
        }

        /// <summary>
        /// Adds reminder storage backed by Azure Table Storage.
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
        public static ISiloHostBuilder UseAzureTableReminderService(this ISiloHostBuilder builder, string connectionString)
        {
            builder.UseAzureTableReminderService(options => options.ConnectionString = connectionString);
            return builder;
        }

        /// <summary>
        /// Adds reminder storage backed by Azure Table Storage.
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
        public static IServiceCollection UseAzureTableReminderService(this IServiceCollection services, Action<AzureTableReminderStorageOptions> configure)
        {
            services.AddSingleton<IReminderTable, AzureBasedReminderTable>();
            services.Configure(configure);
            services.TryConfigureFormatter<AzureTableReminderStorageOptions, AzureTableReminderStorageOptionsFormatter>();
            return services;
        }

        /// <summary>
        /// Adds reminder storage backed by Azure Table Storage.
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
        public static IServiceCollection UseAzureTableReminderService(this IServiceCollection services, string connectionString)
        {
            services.UseAzureTableReminderService(options => options.ConnectionString = connectionString);
            return services;
        }
    }
}