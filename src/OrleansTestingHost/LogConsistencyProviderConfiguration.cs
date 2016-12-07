
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Orleans.Runtime;

namespace Orleans.TestingHost
{
    /// <summary> A static class with functionality shared by various log-consistency provider tests.  </summary>
    public static class LogConsistencyProviderConfiguration
    {
        // change this as needed for debugging failing tests
        private const Severity LogConsistencyProviderTraceLevel = Severity.Verbose2;

        /// <summary>
        /// Initializes a bunch of different
        /// log consistency providers with different configuration settings.
        /// </summary>
        /// <param name="dataConnectionString">the data connection string</param>
        /// <param name="config">The configuration to modify</param>
        public static void ConfigureLogConsistencyProvidersForTesting(string dataConnectionString, ClusterConfiguration config)
        {
            {
                var props = new Dictionary<string, string>();
                props.Add("DataConnectionString", dataConnectionString);
                config.Globals.RegisterStorageProvider("Orleans.Storage.AzureTableStorage", "AzureStore", props);
            }
            {
                var props = new Dictionary<string, string>();
                config.Globals.RegisterLogConsistencyProvider("Orleans.EventSourcing.StateStorage.LogConsistencyProvider", "StateStorage", props);
            }
            {
                var props = new Dictionary<string, string>();
                config.Globals.RegisterLogConsistencyProvider("Orleans.EventSourcing.LogStorage.LogConsistencyProvider", "LogStorage", props);
            }
            {
                var props = new Dictionary<string, string>();
                config.Globals.RegisterLogConsistencyProvider("Orleans.EventSourcing.CustomStorage.LogConsistencyProvider", "CustomStorage", props);
            }
            {
                var props = new Dictionary<string, string>();
                props.Add("PrimaryCluster", "A");
                config.Globals.RegisterLogConsistencyProvider("Orleans.EventSourcing.CustomStorage.LogConsistencyProvider", "CustomStoragePrimaryCluster", props);
            }

            // logging  
            foreach (var o in config.Overrides)
                o.Value.TraceLevelOverrides.Add(new Tuple<string, Severity>("LogViews", Severity.Verbose2));

        }
    }
}
