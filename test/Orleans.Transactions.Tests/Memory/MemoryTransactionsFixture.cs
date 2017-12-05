using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Orleans.Hosting;
using Orleans.Hosting.Development;
using Orleans.TestingHost.Utils;
using TestExtensions;

namespace Orleans.Transactions.Tests
{
    public class MemoryTransactionsFixture : BaseTestClusterFixture
    {
        protected override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions();
            options.ClusterConfiguration.AddMemoryStorageProvider(TransactionTestConstants.TransactionStore);
            options.UseSiloBuilderFactory<SiloBuilderFactory>();
            return new TestCluster(options);
        }

        private class SiloBuilderFactory : ISiloBuilderFactory
        {
            public ISiloHostBuilder CreateSiloBuilder(string siloName, ClusterConfiguration clusterConfiguration)
            {
                return new SiloHostBuilder()
                    .ConfigureSiloName(siloName)
                    .UseConfiguration(clusterConfiguration)
                    .ConfigureLogging(builder => TestingUtils.ConfigureDefaultLoggingBuilder(builder, TestingUtils.CreateTraceFileName(siloName, clusterConfiguration.Globals.ClusterId)))
                    .UseInClusterTransactionManager()
                    .UseInMemoryTransactionLog()
                    .UseTransactionalState();
            }
        }
    }
}
