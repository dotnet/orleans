using Orleans.TestingHost;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TestExtensions;
using Orleans.Runtime.Configuration;
using Orleans.Runtime;

namespace EventSourcing.Tests
{
    /// <summary>
    /// We use a special fixture for event sourcing tests 
    /// so we can add the required log consistency providers, and 
    /// do more tracing
    /// </summary>
    public class EventSourcingClusterFixture : BaseTestClusterFixture
    {
        protected override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions();

            options.ClusterConfiguration.AddMemoryStorageProvider("Default");
            options.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore");
            options.ClusterConfiguration.AddFaultyMemoryStorageProvider("SlowMemoryStore", 10, 15);

            // configure event storage providers
            options.ClusterConfiguration.AddMemoryEventStorageProvider("Default");
            options.ClusterConfiguration.AddMemoryEventStorageProvider("MemoryEventStore");
            options.ClusterConfiguration.AddFaultyMemoryEventStorageProvider("SlowMemoryEventStore", 10, 15);

            // log consistency providers are used to configure journaled grains
            options.ClusterConfiguration.AddEventStorageBasedLogConsistencyProvider("Default");
            options.ClusterConfiguration.AddEventStorageBasedLogConsistencyProvider("EventStorage");
            options.ClusterConfiguration.AddStateStorageBasedLogConsistencyProvider("StateStorage");
            options.ClusterConfiguration.AddLogStorageBasedLogConsistencyProvider("LogStorage");


            // we turn on extra logging  to see more tracing from the log consistency providers
            foreach (var o in options.ClusterConfiguration.Overrides)
            {
                o.Value.TraceLevelOverrides.Add(new Tuple<string, Severity>("Storage.MemoryStorage", Severity.Verbose));
                o.Value.TraceLevelOverrides.Add(new Tuple<string, Severity>("Storage.MemoryEventStorage", Severity.Verbose));
                o.Value.TraceLevelOverrides.Add(new Tuple<string, Severity>("LogViews", Severity.Verbose));
            }

            return new TestCluster(options);
        }
    }
}
