using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Reminders.AzureStorage;
using Orleans.Runtime.ReminderService;
namespace Orleans.Hosting
{
    /// <summary>
    /// <see cref="IServiceCollection"/> extensions.
    /// </summary>
    public static class AzureStorageReminderServiceCollectionExtensions
    {
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
        /// The provided <see cref="IServiceCollection"/>, for chaining.
        /// </returns>
        public static IServiceCollection UseAzureTableReminderService(this IServiceCollection services, Action<AzureTableReminderStorageOptions> configure)
        {
            services.AddReminders();
            services.AddSingleton<IReminderTable, AzureBasedReminderTable>();
            services.Configure<AzureTableReminderStorageOptions>(configure);
            services.ConfigureFormatter<AzureTableReminderStorageOptions>();
            return services;
        }

        /// <summary>
        /// Adds reminder storage backed by Azure Table Storage.
        /// </summary>
        /// <param name="services">
        /// The service collection.
        /// </param>
        /// <param name="configureOptions">
        /// The configuration delegate.
        /// </param>
        /// <returns>
        /// The provided <see cref="IServiceCollection"/>, for chaining.
        /// </returns>
        public static IServiceCollection UseAzureTableReminderService(this IServiceCollection services, Action<OptionsBuilder<AzureTableReminderStorageOptions>> configureOptions)
        {
            services.AddReminders();
            services.AddSingleton<IReminderTable, AzureBasedReminderTable>();
            configureOptions?.Invoke(services.AddOptions<AzureTableReminderStorageOptions>());
            services.ConfigureFormatter<AzureTableReminderStorageOptions>();
            services.AddTransient<IConfigurationValidator>(sp => new AzureTableReminderStorageOptionsValidator(sp.GetRequiredService<IOptionsMonitor<AzureTableReminderStorageOptions>>().Get(Options.DefaultName), Options.DefaultName));
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
        /// The provided <see cref="IServiceCollection"/>, for chaining.
        /// </returns>
        public static IServiceCollection UseAzureTableReminderService(this IServiceCollection services, string connectionString)
        {
            services.UseAzureTableReminderService(options => options.ConfigureTableServiceClient(connectionString));
            return services;
        }
    }
}
