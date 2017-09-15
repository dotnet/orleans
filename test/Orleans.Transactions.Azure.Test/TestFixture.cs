using Orleans.Runtime.Configuration;
using Orleans.Hosting;
using Orleans.TestingHost;
using TestExtensions;
using Orleans.Transactions.Tests;
using Orleans.TestingHost.Utils;

namespace Orleans.Transactions.Azure.Tests
{
    public class TestFixture : BaseTestClusterFixture
    {
        protected override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions();
            options.ClusterConfiguration.AddAzureTableStorageProvider(TransactionTestConstants.TransactionStore);
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
                    .ConfigureLogging(builder => TestingUtils.ConfigureDefaultLoggingBuilder(builder, clusterConfiguration.GetOrCreateNodeConfigurationForSilo(siloName).TraceFileName))
                    .UseInClusterTransactionManager(new TransactionsConfiguration())
                    .UseAzureTransactionLog(new AzureTransactionLogConfiguration() {
                        // TODO: Find better way for test isolation.  Possibly different partition keys.
                        TableName = $"TransactionLog{((uint)clusterConfiguration.Globals.DeploymentId.GetHashCode())%100000}",
                        ConnectionString = TestDefaultConfiguration.DataConnectionString})
                    .UseTransactionalState();
            }
        }
    }
}
