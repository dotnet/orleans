using Orleans.TestingHost;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using TestExtensions;
using Orleans.Runtime.Configuration;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.TestingHost.Utils;

namespace Tester.EventSourcingTests
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

            // we use a slowed-down memory storage provider
            options.ClusterConfiguration.AddMemoryStorageProvider("Default");
            options.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore");

            options.ClusterConfiguration.AddFaultyMemoryStorageProvider("SlowMemoryStore", 10, 15);

            // log consistency providers are used to configure journaled grains
            options.ClusterConfiguration.AddLogStorageBasedLogConsistencyProvider("LogStorage");
            options.ClusterConfiguration.AddStateStorageBasedLogConsistencyProvider("StateStorage");
            
            options.UseSiloBuilderFactory<TestSiloBuilderFactory>();
            return new TestCluster(options);
        }

        private class TestSiloBuilderFactory : ISiloBuilderFactory
        {
            public ISiloHostBuilder CreateSiloBuilder(string siloName, ClusterConfiguration clusterConfiguration)
            {
                return new SiloHostBuilder()
                    .ConfigureSiloName(siloName)
                    .UseConfiguration(clusterConfiguration)
                    .ConfigureLogging(builder => ConfigureLogging(builder, clusterConfiguration.GetOrCreateNodeConfigurationForSilo(siloName).TraceFileName));
            }

            private void ConfigureLogging(ILoggingBuilder builder, string filePath)
            {
                TestingUtils.ConfigureDefaultLoggingBuilder(builder, filePath);
                builder.AddFilter(typeof(MemoryStorage).FullName, LogLevel.Debug);
                builder.AddFilter("LogViews", LogLevel.Debug);
            }
        }

    }
}
