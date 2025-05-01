using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Persistence.AzureStorage.Migration.Reminders.Storage;
using Orleans.Reminders.AzureStorage;
using Orleans.Reminders.AzureStorage.Storage.Reminders;
using Orleans.Runtime;
using Orleans.Runtime.ReminderService;

namespace Orleans.Persistence.AzureStorage.Migration
{
    /// <summary>
    /// Silo host builder extensions.
    /// </summary>
    public static class AzureTableSiloBuilderExtensions
    {
        /// <summary>
        /// Adds reminder storage backed by Azure Table Storage.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="name">name of this azure table reminder registration</param>
        /// <param name="configureOptions">The configuration delegate.</param>
        /// <returns>The provided <see cref="ISiloBuilder"/>, for chaining.</returns>
        public static ISiloBuilder UseAzureTableReminderService(this ISiloBuilder builder, string name, Action<AzureTableReminderStorageOptions> configureOptions)
        {
            builder.ConfigureServices(services => services.UseAzureTableReminderService(name, configureOptions));
            return builder;
        }

        /// <summary>
        /// Adds reminder storage backed by Azure Table Storage.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="name">name of this azure table reminder registration</param>
        /// <param name="configureOptions">The configuration delegate.</param>
        /// <returns>The provided <see cref="ISiloBuilder"/>, for chaining.</returns>
        public static ISiloBuilder UseAzureTableReminderService(this ISiloBuilder builder, string name, Action<OptionsBuilder<AzureTableReminderStorageOptions>> configureOptions)
        {
            builder.ConfigureServices(services => services.UseAzureTableReminderService(name, configureOptions));
            return builder;
        }

        /// <summary>
        /// Adds reminder storage backed by Azure Table Storage.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="name">name of this azure table reminder registration</param>
        /// <param name="configure">The delegate used to configure the reminder store</param>
        /// <returns></returns>
        public static IServiceCollection UseAzureTableReminderService(this IServiceCollection services, string name, Action<AzureTableReminderStorageOptions> configure)
            => services.UseAzureTableReminderService(name, ob => ob.Configure(configure));

        /// <summary>
        /// Adds reminder storage backed by Azure Table Storage.
        /// </summary>
        /// <param name="services">
        /// The service collection.
        /// </param>
        /// <param name="name">name of current azure table reminder registration</param>
        /// <param name="configureOptions">
        /// The configuration delegate.
        /// </param>
        /// <returns>
        /// The provided <see cref="IServiceCollection"/>, for chaining.
        /// </returns>
        public static IServiceCollection UseAzureTableReminderService(this IServiceCollection services, string name, Action<OptionsBuilder<AzureTableReminderStorageOptions>> configureOptions)
        {
            configureOptions?.Invoke(services.AddOptions<AzureTableReminderStorageOptions>(name));
            services.AddSingletonNamedService(name, AzureBasedReminderTable.Create);
            return services;
        }

        /// <summary>
        /// Use Azure Table Storage for migrated Reminder's data and current data.
        /// </summary>
        public static ISiloBuilder UseMigrationAzureTableReminderStorage(
            this ISiloBuilder builder,
            string name,
            Action<AzureTableReminderStorageOptions> configureStorageOptions)
        {
            return builder.ConfigureServices(services => services.UseMigrationAzureTableReminderStorage(name, configureStorageOptions));
        }

        /// <summary>
        /// Configure silo to use migration storage for grain storage.
        /// </summary>
        public static IServiceCollection UseMigrationAzureTableReminderStorage(this IServiceCollection services, string name, Action<AzureTableReminderStorageOptions> configureOptions)
        {
            return services.UseMigrationAzureTableReminderStorage(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Use Azure Table Storage for migrated Reminder's data and current data
        /// </summary>
        public static IServiceCollection UseMigrationAzureTableReminderStorage(
            this IServiceCollection services,
            string name,
            Action<OptionsBuilder<AzureTableReminderStorageOptions>> configureOptions)
        {
            configureOptions?.Invoke(services.AddOptions<AzureTableReminderStorageOptions>(name));
            services.AddSingletonNamedService(name, MigrationAzureTableReminderTable.Create);

            return services;
        }
    }
}
