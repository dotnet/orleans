using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.GrainDirectory.Redis;
using Orleans.Runtime;

namespace Orleans.Hosting
{
    public static class RedisGrainDirectoryExtensions
    {
        /// <summary>
        /// Use a Redis data-store as the default Grain Directory
        /// </summary>
        public static ISiloBuilder UseRedisGrainDirectoryAsDefault(
            this ISiloBuilder builder,
            Action<RedisGrainDirectoryOptions> configureOptions)
        {
            return builder.UseRedisGrainDirectoryAsDefault(ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Use a Redis data-store as the default Grain Directory
        /// </summary>
        public static ISiloBuilder UseRedisGrainDirectoryAsDefault(
            this ISiloBuilder builder,
            Action<OptionsBuilder<RedisGrainDirectoryOptions>> configureOptions)
        {
            return builder.ConfigureServices(services => services.AddRedisGrainDirectory(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY, configureOptions));
        }

        /// <summary>
        /// Add a Redis data-store as a named Grain Directory
        /// </summary>
        public static ISiloBuilder AddRedisGrainDirectory(
            this ISiloBuilder builder,
            string name,
            Action<RedisGrainDirectoryOptions> configureOptions)
        {
            return builder.AddRedisGrainDirectory(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Add a Redis data-store as a named Grain Directory
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
                .AddTransient<IConfigurationValidator>(sp => new RedisGrainDirectoryOptionsValidator(sp.GetRequiredService<IOptionsMonitor<RedisGrainDirectoryOptions>>().Get(name)))
                .ConfigureNamedOptionForLogging<RedisGrainDirectoryOptions>(name)
                .AddSingletonNamedService<IGrainDirectory>(name, (sp, name) => ActivatorUtilities.CreateInstance<RedisGrainDirectory>(sp, sp.GetOptionsByName<RedisGrainDirectoryOptions>(name)))
                .AddSingletonNamedService<ILifecycleParticipant<ISiloLifecycle>>(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<IGrainDirectory>(n));

            return services;
        }
    }
}
