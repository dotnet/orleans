using Orleans.Hosting;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;

namespace TestExtensions
{
    public class DefaultClusterFixture : BaseTestClusterFixture
    {
        protected override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions();
            options.ClusterConfiguration.AddMemoryStorageProvider("Default");
            options.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore");
            return new TestCluster(options).UseSiloBuilderFactory<SiloBuilderFactory>();
        }

        public class SiloBuilderFactory : ISiloBuilderFactory
        {
            public ISiloHostBuilder CreateSiloBuilder(string siloName, ClusterConfiguration clusterConfiguration)
            {
                return new SiloHostBuilder()
                    .ConfigureSiloName(siloName)
                    .UseConfiguration(clusterConfiguration)
                    .ConfigureLogging(loggingBuilder => TestingUtils.ConfigureDefaultLoggingBuilder(loggingBuilder, TestingUtils.CreateTraceFileName(siloName, clusterConfiguration.Globals.ClusterId)));
            }
        }
    }
}
