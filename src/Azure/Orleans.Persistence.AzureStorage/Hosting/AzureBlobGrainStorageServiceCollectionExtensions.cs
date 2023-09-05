using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Hosting
{
    /// <summary>
    /// <see cref="IServiceCollection"/> extensions.
    /// </summary>
    public static class AzureBlobGrainStorageServiceCollectionExtensions
    {
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
            services.AddTransient<IConfigurationValidator>(sp => new AzureBlobStorageOptionsValidator(sp.GetRequiredService<IOptionsMonitor<AzureBlobStorageOptions>>().Get(name), name));
            services.AddTransient<IPostConfigureOptions<AzureBlobStorageOptions>, DefaultStorageProviderSerializerOptionsConfigurator<AzureBlobStorageOptions>>();
            services.ConfigureNamedOptionForLogging<AzureBlobStorageOptions>(name);
            if (string.Equals(name, ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, StringComparison.Ordinal))
            {
                services.TryAddSingleton(sp => sp.GetServiceByName<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
            }
            return services.AddSingletonNamedService<IGrainStorage>(name, AzureBlobGrainStorageFactory.Create)
                           .AddSingletonNamedService<ILifecycleParticipant<ISiloLifecycle>>(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<IGrainStorage>(n));
        }
    }
}
