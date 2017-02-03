using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Runtime.Configuration
{ 

    /// <summary>
    /// Extension methods for configuration classes specific to OrleansEventSourcing.dll 
    /// </summary>
    public static class LogConsistencyConfigurationExtensions
    {
        /// <summary>
        /// Adds a log consistency provider of type <see cref="Orleans.EventSourcing.LogStorage.LogConsistencyProvider"/>
        /// </summary>
        /// <param name="config">The cluster configuration object to add provider to.</param>
        /// <param name="providerName">The provider name.</param>
        public static void AddLogStorageBasedLogConsistencyProvider(
            this ClusterConfiguration config,
            string providerName = "LogStorage")
        {
            if (string.IsNullOrWhiteSpace(providerName)) throw new ArgumentNullException(nameof(providerName));

            config.Globals.RegisterLogConsistencyProvider<EventSourcing.LogStorage.LogConsistencyProvider>(providerName);
        }

        /// <summary>
        /// Adds a log consistency provider of type <see cref="Orleans.EventSourcing.StateStorage.LogConsistencyProvider"/>
        /// </summary>
        /// <param name="config">The cluster configuration object to add provider to.</param>
        /// <param name="providerName">The provider name.</param>
        public static void AddStateStorageBasedLogConsistencyProvider(
            this ClusterConfiguration config,
            string providerName = "StateStorage")
        {
            if (string.IsNullOrWhiteSpace(providerName)) throw new ArgumentNullException(nameof(providerName));

            config.Globals.RegisterLogConsistencyProvider<EventSourcing.StateStorage.LogConsistencyProvider>(providerName);
        }

        /// <summary>
        /// Adds a log consistency provider of type <see cref="Orleans.EventSourcing.CustomStorage.LogConsistencyProvider"/>
        /// </summary>
        /// <param name="config">The cluster configuration object to add provider to.</param>
        /// <param name="providerName">The provider name.</param>
        /// <param name="numStorageGrains">The number of storage grains to use.</param>
        public static void AddCustomStorageInterfaceBasedLogConsistencyProvider(
            this ClusterConfiguration config,
            string providerName = "LogStorage",
            string primaryCluster = null)
        {
            if (string.IsNullOrWhiteSpace(providerName)) throw new ArgumentNullException(nameof(providerName));

            var properties = new Dictionary<string, string>();

            if (primaryCluster != null)
                properties.Add("PrimaryCluster", primaryCluster);

            config.Globals.RegisterLogConsistencyProvider<EventSourcing.CustomStorage.LogConsistencyProvider>(providerName, properties);
        }
    }
}
