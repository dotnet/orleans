using Microsoft.Extensions.Logging;
using Orleans.EventSourcing.CustomStorage;
using Orleans.Storage;
using Orleans.TestingHost;
using TestExtensions;

namespace Tester.EventSourcingTests
{
    /// <summary>
    /// We use a special fixture for event sourcing tests 
    /// so we can add the required log consistency providers, and 
    /// do more tracing
    /// </summary>
    public class EventSourcingClusterFixture : BaseTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        }

        private class TestSiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                // we use a slowed-down memory storage provider
                hostBuilder
                    .AddLogStorageBasedLogConsistencyProvider("LogStorage")
                    .AddStateStorageBasedLogConsistencyProvider("StateStorage")
                    .AddCustomStorageBasedLogConsistencyProvider("CustomStoragePrimaryCluster")
                    .ConfigureLogging(builder =>
                    {
                        // Capture worker phases, storage-provider entry, and storage-grain receipt for #11398.
                        builder.AddFilter("Orleans.Runtime.LogConsistency", LogLevel.Debug);
                        builder.AddFilter(typeof(MemoryGrainStorage).Namespace, LogLevel.Trace);
                    })
                    .AddMemoryGrainStorageAsDefault()
                    .AddMemoryGrainStorage("AzureStore")
                    .AddMemoryGrainStorage("MemoryStore")
                    .AddFaultInjectionMemoryStorage("SlowMemoryStore", options => options.NumStorageGrains = 10, faultyOptions => faultyOptions.Latency = TimeSpan.FromMilliseconds(15));
            }
        }
    }
}
