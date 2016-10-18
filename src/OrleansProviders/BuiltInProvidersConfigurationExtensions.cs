using System;
using System.Collections.Generic;
using Orleans.Storage;

namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// Extension methods for configuration classes specific to OrleansProviders.dll 
    /// </summary>
    public static class BuiltInProvidersConfigurationExtensions
    {
        /// <summary>
        /// Adds a storage provider of type <see cref="MemoryStorage"/>
        /// </summary>
        /// <param name="config">The cluster configuration object to add provider to.</param>
        /// <param name="providerName">The provider name.</param>
        /// <param name="numStorageGrains">The number of storage grains to use.</param>
        public static void AddMemoryStorageProvider(
            this ClusterConfiguration config,
            string providerName = "MemoryStore",
            int numStorageGrains = MemoryStorage.NumStorageGrainsDefaultValue)
        {
            if (string.IsNullOrWhiteSpace(providerName)) throw new ArgumentNullException(nameof(providerName));

            var properties = new Dictionary<string, string>
            {
                { MemoryStorage.NumStorageGrainsPropertyName, numStorageGrains.ToString() },
            };

            config.Globals.RegisterStorageProvider<MemoryStorage>(providerName, properties);
        }
    }
}