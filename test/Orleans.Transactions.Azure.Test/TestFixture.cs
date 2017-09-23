using Orleans.Runtime.Configuration;
using Orleans.Hosting;
using Orleans.TestingHost;
using TestExtensions;
using Orleans.Transactions.Tests;

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
            public ISiloBuilder CreateSiloBuilder(string siloName, ClusterConfiguration clusterConfiguration)
            {
                return new SiloBuilder()
                    .ConfigureSiloName(siloName)
                    .UseConfiguration(clusterConfiguration)
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
