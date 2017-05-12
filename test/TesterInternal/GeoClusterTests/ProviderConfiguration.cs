
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Orleans.Runtime;

namespace Tests.GeoClusterTests
{
    /// <summary> A static class with functionality shared by various log-consistency provider tests.  </summary>
    public static class ProviderConfiguration
    {
        // change this as needed for debugging failing tests
        private const Severity LogConsistencyProviderTraceLevel = Severity.Verbose2;

        /// <summary>
        /// Initializes a bunch of different
        /// log consistency providers with different configuration settings.
        /// </summary>
        /// <param name="dataConnectionString">the data connection string</param>
        /// <param name="config">The configuration to modify</param>
        public static void ConfigureProvidersForTesting(this ClusterConfiguration config, string dataConnectionString)
        {
            // event-storage providers
            config.AddMemoryEventStorageProvider("Default");
            config.AddMemoryEventStorageProvider("MemoryEventStore");

            // storage providers
            config.AddAzureTableStorageProvider("AzureStore", dataConnectionString);

            // log-consistency providers
            config.AddEventStorageBasedLogConsistencyProvider("Default");
            config.AddEventStorageBasedLogConsistencyProvider("EventStorage");
            config.AddLogStorageBasedLogConsistencyProvider("LogStorage");
            config.AddStateStorageBasedLogConsistencyProvider("StateStorage");
            config.AddCustomStorageInterfaceBasedLogConsistencyProvider("CustomStorage");
            config.AddCustomStorageInterfaceBasedLogConsistencyProvider("CustomStoragePrimaryCluster", "A");

        }
    }
}
