using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Hosting
{
    /// <summary>
    /// Silo host builder extensions
    /// </summary>
    public static class MemoryGrainStorageSiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use memory grain storage as the default grain storage.
        /// </summary>
        public static ISiloHostBuilder AddMemoryGrainStorageAsDefault(this ISiloHostBuilder builder, Action<MemoryGrainStorageOptions> configureOptions)
        {
            return builder
                .ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(MemoryStorageGrain).Assembly))
                .AddMemoryGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use memory grain storage as the default grain storage.
        /// </summary>
        public static ISiloHostBuilder AddMemoryGrainStorage(this ISiloHostBuilder builder, string name, Action<MemoryGrainStorageOptions> configureOptions)
        {
            return builder
                .ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(MemoryStorageGrain).Assembly))
                .ConfigureServices(services => services.AddMemoryGrainStorage(name, ob => ob.Configure(configureOptions)));
        }

        /// <summary>
        /// Configure silo to use memory grain storage as the default grain storage.
        /// </summary>
        public static ISiloHostBuilder AddMemoryGrainStorageAsDefault(this ISiloHostBuilder builder, Action<OptionsBuilder<MemoryGrainStorageOptions>> configureOptions = null)
        {
            return builder
                .ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(MemoryStorageGrain).Assembly))
                .AddMemoryGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        ///Configure silo to use memory grain storage as the default grain storage.
        /// </summary>
        public static ISiloHostBuilder AddMemoryGrainStorage(this ISiloHostBuilder builder, string name, Action<OptionsBuilder<MemoryGrainStorageOptions>> configureOptions = null)
        {
            return builder
                .ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(MemoryStorageGrain).Assembly))
                .ConfigureServices(services => services.AddMemoryGrainStorage(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use memory grain storage as the default grain storage.
        /// </summary>
        private static void AddMemoryGrainStorage(this IServiceCollection services, string name,
            Action<OptionsBuilder<MemoryGrainStorageOptions>> configureOptions = null)
        {
            configureOptions?.Invoke(services.AddOptions<MemoryGrainStorageOptions>(name));
            services.ConfigureNamedOptionForLogging<MemoryGrainStorageOptions>(name);
            services.TryAddSingleton<IGrainStorage>(sp => sp.GetServiceByName<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
            services.AddSingletonNamedService<IGrainStorage>(name, MemoryGrainStorageFactory.Create);
        }
    }
}
