using System;
using System.Collections.Generic;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// Extension methods for configuration classes specific to Orleans.Streaming.AzureStorage.dll 
    /// </summary>
    public static class AzureConfigurationExtensions
    {
        /// <summary>
        /// Adds a stream provider of type <see cref="AzureQueueStreamProvider"/>.
        /// </summary>
        /// <param name="config">The cluster configuration object to add provider to.</param>
        /// <param name="providerName">The provider name</param>
        /// <param name="connectionString">The azure storage connection string. If none is provided, it will use the same as in the Globals configuration.</param>
        /// <param name="numberOfQueues">The number of queues to use as partitions.</param>
        /// <param name="clusterId">The ClusterId used for partitioning. If none is specified, the provider will use the same ClusterId as the Cluster.</param>
        /// <param name="cacheSize">The cache size.</param>
        /// <param name="startupState">The startup state of the persistent stream provider.</param>
        /// <param name="persistentStreamProviderConfig">Settings related to all persistent stream providers.</param>
        public static void AddAzureQueueStreamProvider(
            this ClusterConfiguration config,
            string providerName,
            string connectionString = null,
            int numberOfQueues = AzureQueueAdapterConstants.NumQueuesDefaultValue,
            string clusterId = null,
            int cacheSize = AzureQueueAdapterConstants.CacheSizeDefaultValue,
#pragma warning disable CS0618 // Type or member is obsolete
            PersistentStreamProviderState startupState = AzureQueueStreamProvider.StartupStateDefaultValue,
#pragma warning restore CS0618 // Type or member is obsolete
            PersistentStreamProviderConfig persistentStreamProviderConfig = null)
        {
            connectionString = GetConnectionString(connectionString, config);
            clusterId = clusterId ?? config.Globals.ClusterId;
            var properties = GetAzureQueueStreamProviderProperties(providerName, connectionString, numberOfQueues, clusterId, cacheSize, startupState, persistentStreamProviderConfig);
#pragma warning disable 618
            config.Globals.RegisterStreamProvider<AzureQueueStreamProvider>(providerName, properties);
#pragma warning restore 618
        }

        /// <summary>
        /// Adds a stream provider of type <see cref="AzureQueueStreamProviderV2"/>.
        /// </summary>
        /// <param name="config">The cluster configuration object to add provider to.</param>
        /// <param name="providerName">The provider name</param>
        /// <param name="connectionString">The azure storage connection string. If none is provided, it will use the same as in the Globals configuration.</param>
        /// <param name="numberOfQueues">The number of queues to use as partitions.</param>
        /// <param name="clusterId">The ClusterId used for partitioning. If none is specified, the provider will use the same ClusterId as the Cluster.</param>
        /// <param name="cacheSize">The cache size.</param>
        /// <param name="startupState">The startup state of the persistent stream provider.</param>
        /// <param name="persistentStreamProviderConfig">Settings related to all persistent stream providers.</param>
        public static void AddAzureQueueStreamProviderV2(
            this ClusterConfiguration config,
            string providerName,
            string connectionString = null,
            int numberOfQueues = AzureQueueAdapterConstants.NumQueuesDefaultValue,
            string clusterId = null,
            int cacheSize = AzureQueueAdapterConstants.CacheSizeDefaultValue,
#pragma warning disable 618
            PersistentStreamProviderState startupState = AzureQueueStreamProvider.StartupStateDefaultValue,
#pragma warning restore 618
            PersistentStreamProviderConfig persistentStreamProviderConfig = null)
        {
            connectionString = GetConnectionString(connectionString, config);
            clusterId = clusterId ?? config.Globals.ClusterId;
            var properties = GetAzureQueueStreamProviderProperties(providerName, connectionString, numberOfQueues, clusterId, cacheSize, startupState, persistentStreamProviderConfig);
            config.Globals.RegisterStreamProvider<AzureQueueStreamProviderV2>(providerName, properties);
        }

        /// <summary>
        /// Adds a stream provider of type <see cref="AzureQueueStreamProvider"/>.
        /// </summary>
        /// <param name="config">The cluster configuration object to add provider to.</param>
        /// <param name="providerName">The provider name</param>
        /// <param name="connectionString">The azure storage connection string. If none is provided, it will use the same as in the Globals configuration.</param>
        /// <param name="numberOfQueues">The number of queues to use as partitions.</param>
        /// <param name="clusterId">The ClusterId used for partitioning. If none is specified, the provider will use the same ClusterId as the Cluster.</param>
        /// <param name="cacheSize">The cache size.</param>
        /// <param name="startupState">The startup state of the persistent stream provider.</param>
        /// <param name="persistentStreamProviderConfig">Settings related to all persistent stream providers.</param>
        public static void AddAzureQueueStreamProvider(
            this ClientConfiguration config,
            string providerName,
            string connectionString = null,
            int numberOfQueues = AzureQueueAdapterConstants.NumQueuesDefaultValue,
            string clusterId = null,
            int cacheSize = AzureQueueAdapterConstants.CacheSizeDefaultValue,
#pragma warning disable 618
            PersistentStreamProviderState startupState = AzureQueueStreamProvider.StartupStateDefaultValue,
#pragma warning restore 618
            PersistentStreamProviderConfig persistentStreamProviderConfig = null)
        {
            connectionString = GetConnectionString(connectionString, config);
            clusterId = clusterId ?? config.ClusterId;
            var properties = GetAzureQueueStreamProviderProperties(providerName, connectionString, numberOfQueues, clusterId, cacheSize, startupState, persistentStreamProviderConfig);
#pragma warning disable 618
            config.RegisterStreamProvider<AzureQueueStreamProvider>(providerName, properties);
#pragma warning restore 618
        }

        /// <summary>
        /// Adds a stream provider of type <see cref="AzureQueueStreamProviderV2"/>.
        /// </summary>
        /// <param name="config">The cluster configuration object to add provider to.</param>
        /// <param name="providerName">The provider name</param>
        /// <param name="connectionString">The azure storage connection string. If none is provided, it will use the same as in the Globals configuration.</param>
        /// <param name="numberOfQueues">The number of queues to use as partitions.</param>
        /// <param name="clusterId">The ClusterId used for partitioning. If none is specified, the provider will use the same ClusterId as the Cluster.</param>
        /// <param name="cacheSize">The cache size.</param>
        /// <param name="startupState">The startup state of the persistent stream provider.</param>
        /// <param name="persistentStreamProviderConfig">Settings related to all persistent stream providers.</param>
        public static void AddAzureQueueStreamProviderV2(
            this ClientConfiguration config,
            string providerName,
            string connectionString = null,
            int numberOfQueues = AzureQueueAdapterConstants.NumQueuesDefaultValue,
            string clusterId = null,
            int cacheSize = AzureQueueAdapterConstants.CacheSizeDefaultValue,
#pragma warning disable 618
            PersistentStreamProviderState startupState = AzureQueueStreamProvider.StartupStateDefaultValue,
#pragma warning restore 618
            PersistentStreamProviderConfig persistentStreamProviderConfig = null)
        {
            connectionString = GetConnectionString(connectionString, config);
            clusterId = clusterId ?? config.ClusterId;
            var properties = GetAzureQueueStreamProviderProperties(providerName, connectionString, numberOfQueues, clusterId, cacheSize, startupState, persistentStreamProviderConfig);
            config.RegisterStreamProvider<AzureQueueStreamProviderV2>(providerName, properties);
        }

        private static Dictionary<string, string> GetAzureQueueStreamProviderProperties(string providerName, string connectionString, int numberOfQueues, string deploymentId, int cacheSize, PersistentStreamProviderState startupState, PersistentStreamProviderConfig persistentStreamProviderConfig)
        {
            if (string.IsNullOrWhiteSpace(providerName)) throw new ArgumentNullException(nameof(providerName));
            if (numberOfQueues < 1) throw new ArgumentOutOfRangeException(nameof(numberOfQueues));

            var properties = new Dictionary<string, string>
            {
                { AzureQueueAdapterConstants.DataConnectionStringPropertyName, connectionString },
                { AzureQueueAdapterConstants.NumQueuesPropertyName, numberOfQueues.ToString() },
                { AzureQueueAdapterConstants.DeploymentIdPropertyName, deploymentId },
                { SimpleQueueAdapterCache.CacheSizePropertyName, cacheSize.ToString() },
#pragma warning disable 618
                { AzureQueueStreamProvider.StartupStatePropertyName, startupState.ToString() },
#pragma warning restore 618
            };

            persistentStreamProviderConfig?.WriteProperties(properties);

            return properties;
        }

        private static string GetConnectionString(string connectionString, ClusterConfiguration config)
        {
            if (!string.IsNullOrWhiteSpace(connectionString)) return connectionString;
            if (!string.IsNullOrWhiteSpace(config.Globals.DataConnectionString)) return config.Globals.DataConnectionString;

            throw new ArgumentNullException(nameof(connectionString),
                "Parameter value and fallback value are both null or empty.");
        }

        private static string GetConnectionString(string connectionString, ClientConfiguration config)
        {
            if (!string.IsNullOrWhiteSpace(connectionString)) return connectionString;
            if (!string.IsNullOrWhiteSpace(config.DataConnectionString)) return config.DataConnectionString;

            throw new ArgumentNullException(nameof(connectionString),
                "Parameter value and fallback value are both null or empty.");
        }
    }
}