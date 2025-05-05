using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Metadata;
using Orleans.Persistence.AzureStorage.Migration.Reminders;
using Orleans.Persistence.Migration.Serialization;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Serialization.TypeSystem;
using Orleans.Storage;
using CachedTypeResolver = Orleans.Serialization.TypeSystem.CachedTypeResolver;

namespace Orleans.Persistence.Migration
{
    public static class HostingExtensions
    {
        /// <summary>
        /// Adds components required for migration tooling to function correctly
        /// </summary>
        public static ISiloBuilder AddMigrationTools(this ISiloBuilder builder)
            => builder.ConfigureServices(services => services.AddMigrationTools());

        internal static void AddMigrationTools(this IServiceCollection services)
            => services
                .AddSingleton<IPostConfigureOptions<OrleansJsonSerializerOptions>, ConfigureOrleansJsonSerializerOptions>()
                .AddSingleton<OrleansMigrationJsonSerializer>()
                .AddSingleton<IGrainStorageSerializer, JsonGrainStorageSerializer>()
                .AddSingleton<IGrainTypeProvider, AttributeGrainTypeProvider>()
                .AddSingleton<TypeResolver, CachedTypeResolver>()
                .AddSingleton<TypeConverter>()
                .AddSingleton<Orleans.Runtime.Advanced.IGrainTypeResolver, Metadata.GrainTypeResolver>()
                .AddSingleton<Orleans.Runtime.Advanced.IInterfaceTypeResolver, GrainInterfaceTypeResolver>()
                .AddSingleton<IGrainReferenceExtractor, GrainReferenceExtractor>();

        /// <summary>
        /// Configure silo to use migration storage as the default grain storage.
        /// </summary>
        public static ISiloBuilder AddMigrationGrainStorageAsDefault(this ISiloBuilder builder, Action<MigrationGrainStorageOptions> configureOptions)
        {
            return builder.AddMigrationGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use migration storage for grain storage.
        /// </summary>
        public static ISiloBuilder AddMigrationGrainStorage(this ISiloBuilder builder, string name, Action<MigrationGrainStorageOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.AddMigrationGrainStorage(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use migration storage as the default grain storage.
        /// </summary>
        public static ISiloBuilder AddMigrationGrainStorageAsDefault(this ISiloBuilder builder, Action<OptionsBuilder<MigrationGrainStorageOptions>> configureOptions = null)
        {
            return builder.AddMigrationGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use migration storage for grain storage.
        /// </summary>
        public static ISiloBuilder AddMigrationGrainStorage(this ISiloBuilder builder, string name, Action<OptionsBuilder<MigrationGrainStorageOptions>> configureOptions = null)
        {
            return builder.ConfigureServices(services => services.AddMigrationGrainStorage(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use migration storage as the default grain storage.
        /// </summary>
        public static IServiceCollection AddMigrationGrainStorageAsDefault(this IServiceCollection services, Action<MigrationGrainStorageOptions> configureOptions)
        {
            return services.AddMigrationGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use migration storage for grain storage.
        /// </summary>
        public static IServiceCollection AddMigrationGrainStorage(this IServiceCollection services, string name, Action<MigrationGrainStorageOptions> configureOptions)
        {
            return services.AddMigrationGrainStorage(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use migration storage as the default grain storage.
        /// </summary>
        public static IServiceCollection AddMigrationGrainStorage(this IServiceCollection services, Action<OptionsBuilder<MigrationGrainStorageOptions>> configureOptions = null)
        {
            return services.AddMigrationGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use migration storage for grain storage.
        /// </summary>
        public static IServiceCollection AddMigrationGrainStorage(this IServiceCollection services, string name, Action<OptionsBuilder<MigrationGrainStorageOptions>> configureOptions)
        {
            configureOptions?.Invoke(services.AddOptions<MigrationGrainStorageOptions>(name));
            if (string.Equals(name, ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, StringComparison.Ordinal))
            {
                services.TryAddSingleton(sp => sp.GetServiceByName<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
            }
            services.AddSingletonNamedService<IGrainStorage>(name, MigrationGrainStorage.Create);
            return services;
        }

        /// <summary>
        /// Configure silo to use migration storage for grain storage.
        /// </summary>
        public static ISiloBuilder UseMigrationReminderTable(this ISiloBuilder builder, Action<MigrationReminderTableOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseMigrationReminderTable(configureOptions));
        }

        /// <summary>
        /// Configure silo to use migration storage for grain storage.
        /// </summary>
        public static IServiceCollection UseMigrationReminderTable(this IServiceCollection services, Action<MigrationReminderTableOptions> configureOptions)
        {
            return services.UseMigrationReminderTable(ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use migration flow for reminder table.
        /// By default runs in mode "disabled" - turn it on once you are ready to start migration of the reminders.
        /// </summary>
        public static IServiceCollection UseMigrationReminderTable(this IServiceCollection services, Action<OptionsBuilder<MigrationReminderTableOptions>> configureOptions)
        {
            configureOptions?.Invoke(services.AddOptions<MigrationReminderTableOptions>());
            services.AddSingleton<IReminderTable>(sp => MigrationReminderTable.Create(sp));
            return services;
        }

        /// <summary>
        /// Configure a component to migrate inner data in storages.
        /// Should be registered once per each source-destination migration storage pair.
        /// Method registers DataMigrator with "default" name.
        /// </summary>
        /// <param name="builder">The silo builder</param>
        /// <param name="oldStorage">Source storage (one you were already using)</param>
        /// <param name="newStorage">Destination storage (one you would like to migrate to)</param>
        /// <param name="configureOptions">Options of data migrator configuration</param>
        /// <param name="runAsBackgroundService">If true, the migrator will be started as a background service</param>
        public static ISiloBuilder AddDataMigrator(this ISiloBuilder builder, string oldStorage, string newStorage, Action<DataMigratorOptions> configureOptions, bool runAsBackgroundService = false)
            => builder.ConfigureServices(services => services.AddDataMigrator(oldStorage, newStorage, "default", configureOptions, runAsBackgroundService));

        /// <summary>
        /// Configure a component to migrate inner data in storages.
        /// Should be registered once per each source-destination migration storage pair.
        /// </summary>
        /// <param name="builder">The silo builder</param>
        /// <param name="oldStorage">Source storage (one you were already using)</param>
        /// <param name="newStorage">Destination storage (one you would like to migrate to)</param>
        /// <param name="name">This data migrator registration name. Should be unique for each pair of source/destination storages</param>
        /// <param name="configureOptions">Options of data migrator configuration</param>
        /// <param name="runAsBackgroundService">If true, the migrator will be started as a background service</param>
        public static ISiloBuilder AddDataMigrator(this ISiloBuilder builder, string oldStorage, string newStorage, string name, Action<DataMigratorOptions> configureOptions, bool runAsBackgroundService = false)
            => builder.ConfigureServices(services => services.AddDataMigrator(oldStorage, newStorage, name, configureOptions, runAsBackgroundService));

        /// <summary>
        /// Configure a component to migrate inner data in storages.
        /// Should be registered once per each source-destination migration storage pair.
        /// </summary>
        /// <param name="builder">The silo builder</param>
        /// <param name="oldStorage">Source storage (one you were already using)</param>
        /// <param name="newStorage">Destination storage (one you would like to migrate to)</param>
        /// <param name="name">This data migrator registration name. Should be unique for each pair of source/destination storages</param>
        /// <param name="configureOptions">Options of data migrator configuration</param>
        /// <param name="runAsBackgroundService">If true, the migrator will be started as a background service</param>
        public static ISiloBuilder AddDataMigrator(this ISiloBuilder builder, string oldStorage, string newStorage, string name = "default", Action<OptionsBuilder<DataMigratorOptions>> configureOptions = null, bool runAsBackgroundService = false)
            => builder.ConfigureServices(services => services.AddDataMigrator(oldStorage, newStorage, name, configureOptions, runAsBackgroundService));

        public static IServiceCollection AddDataMigrator(this IServiceCollection services, string oldStorageName, string newStorageName, string name = "default", Action<DataMigratorOptions> configureOptions = null, bool runAsBackgroundService = false)
            => services.AddDataMigrator(oldStorageName, newStorageName, name, ob => ob.Configure(configureOptions), runAsBackgroundService);

        /// <summary>
        /// Configure a component to migrate inner data in storages.
        /// Should be registered once per each source-destination migration storage pair.
        /// </summary>
        /// <param name="services">The service collection to add the data migrator to</param>
        /// <param name="oldStorageName">Source storage (one you were already using)</param>
        /// <param name="newStorageName">Destination storage (one you would like to migrate to)</param>
        /// <param name="name">This data migrator registration name. Should be unique for each pair of source/destination storages</param>
        /// <param name="configureOptions">Options of data migrator configuration</param>
        /// <param name="runAsBackgroundService">If true, the migrator will be started as a background service</param>
        public static IServiceCollection AddDataMigrator(
            this IServiceCollection services,
            string oldStorageName,
            string newStorageName,
            string name = "default",
            Action<OptionsBuilder<DataMigratorOptions>> configureOptions = null,
            bool runAsBackgroundService = false)
        {
            configureOptions?.Invoke(services.AddOptions<DataMigratorOptions>(name));

            services.AddSingletonNamedService(name, (sp, name) =>
            {
                var options = sp.GetRequiredService<IOptionsMonitor<DataMigratorOptions>>().Get(name);

                return new DataMigrator(
                    sp.GetService<ILogger<DataMigrator>>(),
                    name,
                    sp.GetRequiredService<IClusterMembershipService>(),
                    sp.GetRequiredService<ILocalSiloDetails>(),
                    sp.GetRequiredServiceByName<IGrainStorage>(oldStorageName),
                    sp.GetRequiredServiceByName<IGrainStorage>(newStorageName),
                    sp.GetService<IReminderTable>(),
                    options);
            });

            if (runAsBackgroundService)
            {
                services.AddHostedService(sp => sp.GetServiceByName<DataMigrator>(name));
            }
            return services;
        }
    }
}
