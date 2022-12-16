using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Persistence;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Hosting
{
    /// <summary>
    /// <see cref="IServiceCollection"/> extensions.
    /// </summary>
    public static class RedisGrainStorageServiceCollectionExtensions
    {
        /// <summary>
        /// Configure silo to use azure blob storage as the default grain storage.
        /// </summary>
        public static IServiceCollection AddRedisGrainStorageAsDefault(this IServiceCollection services, Action<RedisStorageOptions> configureOptions)
        {
            return services.AddRedisGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use azure blob storage for grain storage.
        /// </summary>
        public static IServiceCollection AddRedisGrainStorage(this IServiceCollection services, string name, Action<RedisStorageOptions> configureOptions)
        {
            return services.AddRedisGrainStorage(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use azure blob storage as the default grain storage.
        /// </summary>
        public static IServiceCollection AddRedisGrainStorageAsDefault(this IServiceCollection services, Action<OptionsBuilder<RedisStorageOptions>> configureOptions = null)
        {
            return services.AddRedisGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use azure blob storage for grain storage.
        /// </summary>
        public static IServiceCollection AddRedisGrainStorage(this IServiceCollection services, string name,
            Action<OptionsBuilder<RedisStorageOptions>> configureOptions = null)
        {
            configureOptions?.Invoke(services.AddOptions<RedisStorageOptions>(name));
            services.AddTransient<IConfigurationValidator>(sp => new RedisStorageOptionsValidator(sp.GetRequiredService<IOptionsMonitor<RedisStorageOptions>>().Get(name), name));
            services.AddTransient<IPostConfigureOptions<RedisStorageOptions>, DefaultStorageProviderSerializerOptionsConfigurator<RedisStorageOptions>>();
            services.ConfigureNamedOptionForLogging<RedisStorageOptions>(name);
            if (string.Equals(name, ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, StringComparison.Ordinal))
            {
                services.TryAddSingleton(sp => sp.GetServiceByName<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
            }
            services.AddSingletonNamedService<IGrainStorage>(name, RedisGrainStorageFactory.Create)
                .AddSingletonNamedService<ILifecycleParticipant<ISiloLifecycle>>(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<IGrainStorage>(n));
            return services;
        }
    }
}
