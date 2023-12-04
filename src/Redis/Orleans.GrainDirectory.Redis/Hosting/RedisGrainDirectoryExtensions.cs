using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.GrainDirectory.Redis;
using Orleans.Runtime;
using Orleans.Runtime.Hosting;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extensions for configuring Redis as a grain directory provider.
    /// </summary>
    public static class RedisGrainDirectoryExtensions
    {
        /// <summary>
        /// Adds a default grain directory which persists entries in Redis.
        /// </summary>
        public static ISiloBuilder UseRedisGrainDirectoryAsDefault(
            this ISiloBuilder builder,
            Action<RedisGrainDirectoryOptions> configureOptions)
        {
            return builder.UseRedisGrainDirectoryAsDefault(ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Adds a default grain directory which persists entries in Redis.
        /// </summary>
        public static ISiloBuilder UseRedisGrainDirectoryAsDefault(
            this ISiloBuilder builder,
            Action<OptionsBuilder<RedisGrainDirectoryOptions>> configureOptions)
        {
            return builder.ConfigureServices(services => services.AddRedisGrainDirectory(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY, configureOptions));
        }

        /// <summary>
        /// Adds a named grain directory which persists entries in Redis.
        /// </summary>
        public static ISiloBuilder AddRedisGrainDirectory(
            this ISiloBuilder builder,
            string name,
            Action<RedisGrainDirectoryOptions> configureOptions)
        {
            return builder.AddRedisGrainDirectory(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Adds a named grain directory which persists entries in Redis.
        /// </summary>
        public static ISiloBuilder AddRedisGrainDirectory(
            this ISiloBuilder builder,
            string name,
            Action<OptionsBuilder<RedisGrainDirectoryOptions>> configureOptions)
        {
            return builder.ConfigureServices(services => services.AddRedisGrainDirectory(name, configureOptions));
        }

        private static IServiceCollection AddRedisGrainDirectory(
            this IServiceCollection services,
            string name,
            Action<OptionsBuilder<RedisGrainDirectoryOptions>> configureOptions)
        {
            configureOptions.Invoke(services.AddOptions<RedisGrainDirectoryOptions>(name));
            services
                .AddTransient<IConfigurationValidator>(sp => new RedisGrainDirectoryOptionsValidator(sp.GetRequiredService<IOptionsMonitor<RedisGrainDirectoryOptions>>().Get(name), name))
                .ConfigureNamedOptionForLogging<RedisGrainDirectoryOptions>(name)
                .AddGrainDirectory(name, (sp, key) => ActivatorUtilities.CreateInstance<RedisGrainDirectory>(sp, sp.GetOptionsByName<RedisGrainDirectoryOptions>(key)));

            return services;
        }
    }
}
