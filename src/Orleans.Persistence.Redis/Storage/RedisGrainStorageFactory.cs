using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Storage;
using System;

namespace Orleans.Persistence
{
    /// <summary>
    /// Factory used to create instances of Redis grain storage.
    /// </summary>
    public static class RedisGrainStorageFactory
    {
        /// <summary>
        /// Creates a grain storage instance.
        /// </summary>
        public static IGrainStorage Create(IServiceProvider services, string name)
        {
            var optionsMonitor = services.GetRequiredService<IOptionsMonitor<RedisStorageOptions>>();
            var redisGrainStorage = ActivatorUtilities.CreateInstance<RedisGrainStorage>(services, name, optionsMonitor.Get(name));
            return redisGrainStorage;
        }
    }
}
