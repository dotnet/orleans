using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Orleans.Hosting;
using Orleans.Hosting.Development;
using TestExtensions;

namespace Orleans.Transactions.Tests
{
    public class MemoryTransactionsFixture : BaseTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.ConfigureLegacyConfiguration(legacy =>
            {
                legacy.ClusterConfiguration.AddMemoryStorageProvider(TransactionTestConstants.TransactionStore);
            });
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
        }

        private class SiloBuilderConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder
                    .UseInClusterTransactionManager()
                    .UseInMemoryTransactionLog()
                    .UseTransactionalState();
            }
        }
    }
}
