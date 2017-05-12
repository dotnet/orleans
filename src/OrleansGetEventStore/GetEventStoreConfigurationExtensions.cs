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
    public static class GetEventStoreConfigurationExtensions
    {
        /// <summary>
        /// Adds a log consistency provider for a GetEventStore
        /// </summary>
        /// <param name="config">The cluster configuration object to add provider to.</param>
        /// <param name="providerName">The provider name.</param>
        public static void AddGetEventStoreProvider(
            this ClusterConfiguration config,
            string providerName = "GetEventStore",
            string connectionString = "")
        {
            if (string.IsNullOrWhiteSpace(providerName)) throw new ArgumentNullException(nameof(providerName));

            var properties = new Dictionary<string, string>();

            if (! String.IsNullOrEmpty(connectionString))
                 properties.Add("ConnectionString", connectionString);

            config.Globals.RegisterEventStorageProvider<Orleans.EventSourcing.GetEventStoreProvider>(providerName);
        }
    }
}
