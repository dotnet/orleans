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
    /// <summary>
    /// Silo host builder extensions
    /// </summary>
    public static class MemoryGrainStorageSiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use memory grain storage as the default grain storage.
        /// </summary>
        public static ISiloBuilder AddMemoryGrainStorageAsDefault(this ISiloBuilder builder, Action<MemoryGrainStorageOptions> configureOptions)
        {
            return builder.AddMemoryGrainStorageAsDefault(ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use memory grain storage as the default grain storage.
        /// </summary>
        public static ISiloBuilder AddMemoryGrainStorage(this ISiloBuilder builder, string name, Action<MemoryGrainStorageOptions> configureOptions)
        {
            return builder.AddMemoryGrainStorage(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use memory grain storage as the default grain storage.
        /// </summary>
        public static ISiloBuilder AddMemoryGrainStorageAsDefault(this ISiloBuilder builder, Action<OptionsBuilder<MemoryGrainStorageOptions>> configureOptions = null)
        {
            return builder.AddMemoryGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        ///Configure silo to use memory grain storage as the default grain storage.
        /// </summary>
        public static ISiloBuilder AddMemoryGrainStorage(this ISiloBuilder builder, string name, Action<OptionsBuilder<MemoryGrainStorageOptions>> configureOptions = null)
        {
            return builder
                .ConfigureServices(services =>
                {
                    configureOptions?.Invoke(services.AddOptions<MemoryGrainStorageOptions>(name));
                    services.ConfigureNamedOptionForLogging<MemoryGrainStorageOptions>(name);
                    if (string.Equals(name, ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME))
                        services.TryAddSingleton<IGrainStorage>(sp => sp.GetServiceByName<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
                    services.AddSingletonNamedService<IGrainStorage>(name, MemoryGrainStorageFactory.Create);
                });
        }
    }
}