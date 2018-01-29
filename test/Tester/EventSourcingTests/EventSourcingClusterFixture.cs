using Orleans.TestingHost;
using Microsoft.Extensions.Logging;
using Orleans.EventSourcing.CustomStorage;
using Orleans.Hosting;
using TestExtensions;
using Orleans.Runtime.Configuration;
using Orleans.Storage;

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
            builder.ConfigureLegacyConfiguration(legacy =>
            {
                // we use a slowed-down memory storage provider
                legacy.ClusterConfiguration.AddMemoryStorageProvider("Default");
                legacy.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore");

                legacy.ClusterConfiguration.AddFaultyMemoryStorageProvider("SlowMemoryStore", 10, 15);

                // log consistency providers are used to configure journaled grains
                legacy.ClusterConfiguration.AddLogStorageBasedLogConsistencyProvider("LogStorage");
                legacy.ClusterConfiguration.AddStateStorageBasedLogConsistencyProvider("StateStorage");
            });
            builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        }

        private class TestSiloConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder.ConfigureLogging(builder =>
                {
                    builder.AddFilter(typeof(MemoryStorage).FullName, LogLevel.Debug);
                    builder.AddFilter(typeof(LogConsistencyProvider).Namespace, LogLevel.Debug);
                });
            }
        }

    }
}
