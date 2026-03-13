using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.DurableReminders.AzureStorage;
using Orleans.DurableReminders.Runtime.ReminderService;
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
        public static IServiceCollection UseAzureTableDurableReminderService(this IServiceCollection services, Action<AzureTableReminderStorageOptions> configure)
        {
            services.AddDurableReminders();
            services.AddSingleton<Orleans.DurableReminders.IReminderTable, AzureBasedReminderTable>();
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
        public static IServiceCollection UseAzureTableDurableReminderService(this IServiceCollection services, Action<OptionsBuilder<AzureTableReminderStorageOptions>> configureOptions)
        {
            services.AddDurableReminders();
            services.AddSingleton<Orleans.DurableReminders.IReminderTable, AzureBasedReminderTable>();
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
        public static IServiceCollection UseAzureTableDurableReminderService(this IServiceCollection services, string connectionString)
        {
            services.UseAzureTableDurableReminderService(options =>
            {
                if (Uri.TryCreate(connectionString, UriKind.Absolute, out var uri))
                {
                    options.TableServiceClient = new(uri);
                }
                else
                {
                    options.TableServiceClient = new(connectionString);
                }
            });
            return services;
        }
    }
}
