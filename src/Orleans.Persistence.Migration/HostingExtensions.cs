using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Metadata;
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
        public static ISiloBuilder AddMigrationTools(this ISiloBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services
                    .AddSingleton<IPostConfigureOptions<OrleansJsonSerializerOptions>, ConfigureOrleansJsonSerializerOptions>()
                    .AddSingleton<OrleansMigrationJsonSerializer>()
                    .AddSingleton<IGrainStorageSerializer, JsonGrainStorageSerializer>()
                    .AddSingleton<IGrainTypeProvider, AttributeGrainTypeProvider>()
                    .AddSingleton<TypeResolver, CachedTypeResolver>()
                    .AddSingleton<TypeConverter>()
                    .AddSingleton<Metadata.GrainTypeResolver>()
                    .AddSingleton<GrainInterfaceTypeResolver>()
                    .AddSingleton<IGrainReferenceExtractor, GrainReferenceExtractor>();
            });
            return builder;
        }

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
            services.AddSingleton(sp =>
            {
                return new DataMigrator(
                    sp.GetService<ILogger<DataMigrator>>(),
                    sp.GetRequiredService<IClusterMembershipService>(),
                    sp.GetRequiredService<ILocalSiloDetails>(),
                    sp.GetRequiredServiceByName<IGrainStorage>(oldStorageName),
                    sp.GetRequiredServiceByName<IGrainStorage>(newStorageName),
                    sp.GetService<IReminderMigrationTable>(),
                    options);
            });

            if (options.RunAsBackgroundTask)
            {
                services.AddHostedService<DataMigrator>(sp => sp.GetService<DataMigrator>());
            }

            return services;
        }
    }
}
