using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Orleans.Hosting;
using Orleans.Hosting.Development;
using Orleans.Logging;
using TestExtensions;

namespace Orleans.Transactions.Tests
{
    public class MemoryTransactionsFixture : BaseTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
        }

        public class SiloBuilderConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder
                    .AddMemoryGrainStorage(TransactionTestConstants.TransactionStore)
                    .UseInClusterTransactionManager()
                    .UseInMemoryTransactionLog()
                    .UseTransactionalState();
            }
        }
    }
}
