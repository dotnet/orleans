using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Storage;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration;
using Orleans.Providers;

namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use azure table storage as the default grain storage.
        /// </summary>
        public static ISiloHostBuilder AddAzureTableGrainStorageAsDefault(this ISiloHostBuilder builder, Action<AzureTableStorageOptions> configureOptions)
        {
            return builder.AddAzureTableGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use azure table storage for grain storage.
        /// </summary>
        public static ISiloHostBuilder AddAzureTableGrainStorage(this ISiloHostBuilder builder, string name, Action<AzureTableStorageOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.AddAzureTableGrainStorage(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use azure table storage as the default grain storage.
        /// </summary>
        public static ISiloHostBuilder AddAzureTableGrainStorageAsDefault(this ISiloHostBuilder builder, Action<OptionsBuilder<AzureTableStorageOptions>> configureOptions = null)
        {
            return builder.AddAzureTableGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use azure table storage for grain storage.
        /// </summary>
        public static ISiloHostBuilder AddAzureTableGrainStorage(this ISiloHostBuilder builder, string name, Action<OptionsBuilder<AzureTableStorageOptions>> configureOptions = null)
        {
            return builder.ConfigureServices(services => services.AddAzureTableGrainStorage(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use azure table storage as the default grain storage.
        /// </summary>
        public static IServiceCollection AddAzureTableGrainStorageAsDefault(this IServiceCollection services, Action<AzureTableStorageOptions> configureOptions)
        {
            return services.AddAzureTableGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use azure table storage for grain storage.
        /// </summary>
        public static IServiceCollection AddAzureTableGrainStorage(this IServiceCollection services, string name, Action<AzureTableStorageOptions> configureOptions)
        {
            return services.AddAzureTableGrainStorage(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use azure table storage as the default grain storage.
        /// </summary>
        public static IServiceCollection AddAzureTableGrainStorageAsDefault(this IServiceCollection services, Action<OptionsBuilder<AzureTableStorageOptions>> configureOptions = null)
        {
            return services.AddAzureTableGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use azure table storage for grain storage.
        /// </summary>
        public static IServiceCollection AddAzureTableGrainStorage(this IServiceCollection services, string name,
            Action<OptionsBuilder<AzureTableStorageOptions>> configureOptions = null)
        {
            configureOptions?.Invoke(services.AddOptions<AzureTableStorageOptions>(name));
            services.TryConfigureFormatterResolver<AzureTableStorageOptions, AzureTableStorageOptionsFormatterResolver>();
            services.ConfigureNamedOptionForLogging<AzureTableStorageOptions>(name);
            services.TryAddSingleton<IGrainStorage>(sp => sp.GetServiceByName<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
            return services.AddSingletonNamedService<IGrainStorage>(name, AzureTableGrainStorageFactory.Create)
                           .AddSingletonNamedService<ILifecycleParticipant<ISiloLifecycle>>(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<IGrainStorage>(n));
        }
    }
}
