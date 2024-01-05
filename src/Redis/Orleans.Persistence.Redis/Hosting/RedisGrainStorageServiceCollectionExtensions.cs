using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Persistence;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Hosting;
using Orleans.Storage;

namespace Orleans.Hosting
{
    /// <summary>
    /// <see cref="IServiceCollection"/> extensions.
    /// </summary>
    public static class RedisGrainStorageServiceCollectionExtensions
    {
        /// <summary>
        /// Configures Redis as the default grain storage provider.
        /// </summary>
        public static IServiceCollection AddRedisGrainStorageAsDefault(this IServiceCollection services, Action<RedisStorageOptions> configureOptions)
        {
            return services.AddRedisGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configures Redis as a grain storage provider.
        /// </summary>
        public static IServiceCollection AddRedisGrainStorage(this IServiceCollection services, string name, Action<RedisStorageOptions> configureOptions)
        {
            return services.AddRedisGrainStorage(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configures Redis as the default grain storage provider.
        /// </summary>
        public static IServiceCollection AddRedisGrainStorageAsDefault(this IServiceCollection services, Action<OptionsBuilder<RedisStorageOptions>> configureOptions = null)
        {
            return services.AddRedisGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configures Redis as a grain storage provider.
        /// </summary>
        public static IServiceCollection AddRedisGrainStorage(this IServiceCollection services, string name,
            Action<OptionsBuilder<RedisStorageOptions>> configureOptions = null)
        {
            configureOptions?.Invoke(services.AddOptions<RedisStorageOptions>(name));
            services.AddTransient<IConfigurationValidator>(sp => new RedisStorageOptionsValidator(sp.GetRequiredService<IOptionsMonitor<RedisStorageOptions>>().Get(name), name));
            services.AddTransient<IPostConfigureOptions<RedisStorageOptions>, DefaultStorageProviderSerializerOptionsConfigurator<RedisStorageOptions>>();
            services.ConfigureNamedOptionForLogging<RedisStorageOptions>(name);
            return services.AddGrainStorage(name, RedisGrainStorageFactory.Create);
        }
    }
}
