
using System;
using System.Collections.Generic;
using Orleans.Runtime.Configuration;
using Orleans.Storage;

namespace Orleans.TestingHost
{
    /// <summary>
    /// A memory storage provider that supports injection of storage exceptions.
    /// </summary>
    public class FaultyMemoryStorage : FaultInjectionStorageProvider<MemoryStorage>
    {
    }

    /// <summary>
    /// Extension methods for configuring a FaultyMemoryStorage 
    /// </summary>
    public static class FaultInjectionStorageProviderConfigurationExtensions
    {
        /// <summary>
        /// Adds a storage provider of type <see cref="FaultyMemoryStorage"/>
        /// </summary>
        /// <param name="config">The cluster configuration object to add provider to.</param>
        /// <param name="providerName">The provider name.</param>
        /// <param name="numStorageGrains">The number of storage grains to use.</param>
        /// <param name="delayMilliseconds">A delay to add to each access, in milliseconds</param>
        public static void AddFaultyMemoryStorageProvider(
            this ClusterConfiguration config,
            string providerName = "FaultyMemoryStore",
            int numStorageGrains = MemoryStorage.NumStorageGrainsDefaultValue,
            int delayMilliseconds = 0)
        {
            //TODO: find a way to share the provider configuration setup so we don't have duplicate code.

            if (string.IsNullOrWhiteSpace(providerName)) throw new ArgumentNullException(nameof(providerName));

            var properties = new Dictionary<string, string>
            {
                { MemoryStorage.NumStorageGrainsPropertyName, numStorageGrains.ToString() },
                { FaultyMemoryStorage.DelayMillisecondsPropertyName, delayMilliseconds.ToString() },
            };

            config.Globals.RegisterStorageProvider<FaultyMemoryStorage>(providerName, properties);
        }
    }

}
