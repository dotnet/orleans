using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Persistence.AzureStorage.Migration.Reminders;
using Orleans.Persistence.AzureStorage.Migration.Reminders.Storage;
using Orleans.Persistence.Migration;
using Orleans.Providers;
using Orleans.Reminders.AzureStorage;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.Storage.Migration.AzureStorage;

namespace Orleans.Hosting
{
    public static class AzureBlobSiloBuilderExtensions
    {
        #region Grains

        /// <summary>
        /// Configure silo to use azure blob storage as the default grain storage.
        /// </summary>
        public static ISiloHostBuilder AddMigrationAzureBlobGrainStorageAsDefault(this ISiloHostBuilder builder, Action<AzureBlobStorageOptions> configureOptions)
        {
            return builder.AddMigrationAzureBlobGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use azure blob storage for grain storage.
        /// </summary>
        public static ISiloHostBuilder AddMigrationAzureBlobGrainStorage(this ISiloHostBuilder builder, string name, Action<AzureBlobStorageOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.AddMigrationAzureBlobGrainStorage(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use azure blob storage as the default grain storage.
        /// </summary>
        public static ISiloHostBuilder AddMigrationAzureBlobGrainStorageAsDefault(this ISiloHostBuilder builder, Action<OptionsBuilder<AzureBlobStorageOptions>> configureOptions = null)
        {
            return builder.AddMigrationAzureBlobGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use azure blob storage for grain storage.
        /// </summary>
        public static ISiloHostBuilder AddMigrationAzureBlobGrainStorage(this ISiloHostBuilder builder, string name, Action<OptionsBuilder<AzureBlobStorageOptions>> configureOptions = null)
        {
            return builder.ConfigureServices(services => services.AddMigrationAzureBlobGrainStorage(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use azure blob storage as the default grain storage.
        /// </summary>
        public static ISiloBuilder AddMigrationAzureBlobGrainStorageAsDefault(this ISiloBuilder builder, Action<AzureBlobStorageOptions> configureOptions)
        {
            return builder.AddMigrationAzureBlobGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use azure blob storage for grain storage.
        /// </summary>
        public static ISiloBuilder AddMigrationAzureBlobGrainStorage(this ISiloBuilder builder, string name, Action<AzureBlobStorageOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.AddMigrationAzureBlobGrainStorage(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use azure blob storage as the default grain storage.
        /// </summary>
        public static ISiloBuilder AddMigrationAzureBlobGrainStorageAsDefault(this ISiloBuilder builder, Action<OptionsBuilder<AzureBlobStorageOptions>> configureOptions = null)
        {
            return builder.AddMigrationAzureBlobGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use azure blob storage for grain storage.
        /// </summary>
        public static ISiloBuilder AddMigrationAzureBlobGrainStorage(this ISiloBuilder builder, string name, Action<OptionsBuilder<AzureBlobStorageOptions>> configureOptions = null)
        {
            return builder.ConfigureServices(services => services.AddMigrationAzureBlobGrainStorage(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use azure blob storage as the default grain storage.
        /// </summary>
        public static IServiceCollection AddMigrationAzureBlobGrainStorageAsDefault(this IServiceCollection services, Action<AzureBlobStorageOptions> configureOptions)
        {
            return services.AddMigrationAzureBlobGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use azure blob storage for grain storage.
        /// </summary>
        public static IServiceCollection AddMigrationAzureBlobGrainStorage(this IServiceCollection services, string name, Action<AzureBlobStorageOptions> configureOptions)
        {
            return services.AddMigrationAzureBlobGrainStorage(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use azure blob storage as the default grain storage.
        /// </summary>
        public static IServiceCollection AddMigrationAzureBlobGrainStorageAsDefault(this IServiceCollection services, Action<OptionsBuilder<AzureBlobStorageOptions>> configureOptions = null)
        {
            return services.AddMigrationAzureBlobGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure a component to migrate inner data in storages
        /// </summary>
        public static ISiloBuilder AddDataMigrator(this ISiloBuilder builder, string oldStorage, string newStorage, DataMigrator.Options options = null)
            => builder.ConfigureServices(services => services.AddDataMigrator(oldStorage, newStorage, options));

        /// <summary>
        /// Configure a component to migrate inner data in storages
        /// </summary>
        public static IServiceCollection AddDataMigrator(
            this IServiceCollection services,
            string oldStorageName,
            string newStorageName,
            DataMigrator.Options options = null)
        {
            return services.AddSingleton(sp =>
            {
                return new DataMigrator(
                    sp.GetService<ILogger<DataMigrator>>(),
                    sp.GetRequiredServiceByName<IGrainStorage>(oldStorageName),
                    sp.GetRequiredServiceByName<IGrainStorage>(newStorageName),
                    sp.GetService<IReminderTable>(),
                    options);
            });
        }

        /// <summary>
        /// Configure silo to use azure blob storage for grain storage.
        /// </summary>
        public static IServiceCollection AddMigrationAzureBlobGrainStorage(this IServiceCollection services, string name,
            Action<OptionsBuilder<AzureBlobStorageOptions>> configureOptions = null)
        {
            configureOptions?.Invoke(services.AddOptions<AzureBlobStorageOptions>(name));
            services.AddTransient<IConfigurationValidator>(sp => new AzureBlobStorageOptionsValidator(sp.GetRequiredService<IOptionsMonitor<AzureBlobStorageOptions>>().Get(name), name));
            services.ConfigureNamedOptionForLogging<AzureBlobStorageOptions>(name);
            if (string.Equals(name, ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, StringComparison.Ordinal))
            {
                services.TryAddSingleton(sp => sp.GetServiceByName<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
            }
            return services.AddSingletonNamedService<IGrainStorage>(name, MigrationAzureBlobGrainStorageFactory.Create)
                           .AddSingletonNamedService<ILifecycleParticipant<ISiloLifecycle>>(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<IGrainStorage>(n));
        }

        #endregion

        #region Reminders

        /// <summary>
        /// Use Azure Table Storage for migrated Reminder's data and current data.
        /// </summary>
        public static ISiloBuilder UseMigrationAzureTableReminderStorage(
            this ISiloBuilder builder,
            Action<AzureTableReminderStorageOptions> configureStorageOptions,
            Action<AzureTableMigrationReminderStorageOptions> configureMigratedStorageOptions)
        {
            return builder.ConfigureServices(services => services.UseMigrationAzureTableReminderStorage(configureStorageOptions, configureMigratedStorageOptions));
        }

        /// <summary>
        /// Use Azure Table Storage for migrated Reminder's data and current data
        /// </summary>
        public static IServiceCollection UseMigrationAzureTableReminderStorage(
            this IServiceCollection services,
            Action<AzureTableReminderStorageOptions> configureStorageOptions,
            Action<AzureTableMigrationReminderStorageOptions> configureMigratedStorageOptions)
        {
            services.AddSingleton<IReminderTable, MigrationAzureTableReminderStorage>();
            services.Configure<AzureTableReminderStorageOptions>(configureStorageOptions);
            services.Configure<AzureTableMigrationReminderStorageOptions>(configureMigratedStorageOptions);
            services.ConfigureFormatter<AzureTableReminderStorageOptions>();

            return services;
        }

        #endregion
    }
}
