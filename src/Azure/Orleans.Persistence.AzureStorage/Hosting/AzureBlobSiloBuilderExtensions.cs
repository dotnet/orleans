using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Hosting;
using Orleans.Storage;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Providers;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Hosting
{
    public static class AzureBlobSiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use azure blob storage as the default grain storage.
        /// </summary>
        public static ISiloHostBuilder AddAzureBlobGrainStorageAsDefault(this ISiloHostBuilder builder, Action<AzureBlobStorageOptions> configureOptions)
        {
            return builder.AddAzureBlobGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use azure blob storage for grain storage.
        /// </summary>
        public static ISiloHostBuilder AddAzureBlobGrainStorage(this ISiloHostBuilder builder, string name, Action<AzureBlobStorageOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.AddAzureBlobGrainStorage(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use azure blob storage as the default grain storage.
        /// </summary>
        public static ISiloHostBuilder AddAzureBlobGrainStorageAsDefault(this ISiloHostBuilder builder, Action<OptionsBuilder<AzureBlobStorageOptions>> configureOptions = null)
        {
            return builder.AddAzureBlobGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use azure blob storage for grain storage.
        /// </summary>
        public static ISiloHostBuilder AddAzureBlobGrainStorage(this ISiloHostBuilder builder, string name, Action<OptionsBuilder<AzureBlobStorageOptions>> configureOptions = null)
        {
            return builder.ConfigureServices(services => services.AddAzureBlobGrainStorage(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use azure blob storage as the default grain storage.
        /// </summary>
        public static IServiceCollection AddAzureBlobGrainStorageAsDefault(this IServiceCollection services, Action<AzureBlobStorageOptions> configureOptions)
        {
            return services.AddAzureBlobGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use azure blob storage for grain storage.
        /// </summary>
        public static IServiceCollection AddAzureBlobGrainStorage(this IServiceCollection services, string name, Action<AzureBlobStorageOptions> configureOptions)
        {
            return services.AddAzureBlobGrainStorage(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use azure blob storage as the default grain storage.
        /// </summary>
        public static IServiceCollection AddAzureBlobGrainStorageAsDefault(this IServiceCollection services, Action<OptionsBuilder<AzureBlobStorageOptions>> configureOptions = null)
        {
            return services.AddAzureBlobGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use azure blob storage for grain storage.
        /// </summary>
        public static IServiceCollection AddAzureBlobGrainStorage(this IServiceCollection services, string name,
            Action<OptionsBuilder<AzureBlobStorageOptions>> configureOptions = null)
        {
            configureOptions?.Invoke(services.AddOptions<AzureBlobStorageOptions>(name));
            services.AddTransient<IConfigurationValidator>(sp => new AzureBlobStorageOptionsValidator(sp.GetService<IOptionsSnapshot<AzureBlobStorageOptions>>().Get(name), name));
            services.ConfigureNamedOptionForLogging<AzureBlobStorageOptions>(name);
            services.TryAddSingleton<IGrainStorage>(sp => sp.GetServiceByName<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
            return services.AddSingletonNamedService<IGrainStorage>(name, AzureBlobGrainStorageFactory.Create)
                           .AddSingletonNamedService<ILifecycleParticipant<ISiloLifecycle>>(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<IGrainStorage>(n));
        }
    }
}
