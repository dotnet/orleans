using System;
using Microsoft.Extensions.Options;
using Orleans.Persistence;
using Orleans.Providers;

namespace Orleans.Hosting
{
    /// <summary>
    /// <see cref="ISiloBuilder"/> extensions.
    /// </summary>
    public static class RedisSiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use azure blob storage as the default grain storage.
        /// </summary>
        public static ISiloBuilder AddRedisGrainStorageAsDefault(this ISiloBuilder builder, Action<RedisStorageOptions> configureOptions)
        {
            return builder.AddRedisGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use azure blob storage for grain storage.
        /// </summary>
        public static ISiloBuilder AddRedisGrainStorage(this ISiloBuilder builder, string name, Action<RedisStorageOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.AddRedisGrainStorage(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use azure blob storage as the default grain storage.
        /// </summary>
        public static ISiloBuilder AddRedisGrainStorageAsDefault(this ISiloBuilder builder, Action<OptionsBuilder<RedisStorageOptions>> configureOptions = null)
        {
            return builder.AddRedisGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use azure blob storage for grain storage.
        /// </summary>
        public static ISiloBuilder AddRedisGrainStorage(this ISiloBuilder builder, string name, Action<OptionsBuilder<RedisStorageOptions>> configureOptions = null)
        {
            return builder.ConfigureServices(services => services.AddRedisGrainStorage(name, configureOptions));
        }
    }
}
