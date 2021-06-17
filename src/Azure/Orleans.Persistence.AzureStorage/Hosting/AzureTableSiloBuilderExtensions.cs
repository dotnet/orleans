using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Hosting
{
    public static class AzureTableSiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use azure table storage as the default grain storage.
        /// </summary>
        public static ISiloBuilder AddAzureTableGrainStorageAsDefault(this ISiloBuilder builder, Action<AzureTableStorageOptions> configureOptions)
        {
            return builder.AddAzureTableGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use azure table storage for grain storage.
        /// </summary>
        public static ISiloBuilder AddAzureTableGrainStorage(this ISiloBuilder builder, string name, Action<AzureTableStorageOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.AddAzureTableGrainStorage(name, ob => ob.Configure(configureOptions)));
        }

        /// <summary>
        /// Configure silo to use azure table storage as the default grain storage.
        /// </summary>
        public static ISiloBuilder AddAzureTableGrainStorageAsDefault(this ISiloBuilder builder, Action<OptionsBuilder<AzureTableStorageOptions>> configureOptions = null)
        {
            return builder.AddAzureTableGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use azure table storage for grain storage.
        /// </summary>
        public static ISiloBuilder AddAzureTableGrainStorage(this ISiloBuilder builder, string name, Action<OptionsBuilder<AzureTableStorageOptions>> configureOptions = null)
        {
            return builder.ConfigureServices(services => services.AddAzureTableGrainStorage(name, configureOptions));
        }

        internal static IServiceCollection AddAzureTableGrainStorage(this IServiceCollection services, string name,
            Action<OptionsBuilder<AzureTableStorageOptions>> configureOptions = null)
        {
            configureOptions?.Invoke(services.AddOptions<AzureTableStorageOptions>(name));
            services.AddTransient<IConfigurationValidator>(sp => new AzureTableGrainStorageOptionsValidator(sp.GetRequiredService<IOptionsMonitor<AzureTableStorageOptions>>().Get(name), name));
            services.ConfigureNamedOptionForLogging<AzureTableStorageOptions>(name);
            services.TryAddSingleton<IGrainStorage>(sp => sp.GetServiceByName<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
            return services.AddSingletonNamedService<IGrainStorage>(name, AzureTableGrainStorageFactory.Create)
                           .AddSingletonNamedService<ILifecycleParticipant<ISiloLifecycle>>(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<IGrainStorage>(n));
        }
    }
}
