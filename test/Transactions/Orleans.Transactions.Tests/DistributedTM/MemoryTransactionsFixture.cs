using Orleans.TestingHost;
using Orleans.Hosting;
using TestExtensions;
using Microsoft.Extensions.Logging;

namespace Orleans.Transactions.Tests.DistributedTM
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
                    .ConfigureLogging(builder => builder.AddFilter("SingleStateTransactionalGrain.data", LogLevel.Trace))
                    .ConfigureLogging(builder => builder.AddFilter("TransactionAgent", LogLevel.Trace))
                    .AddMemoryGrainStorage(TransactionTestConstants.TransactionStore)
                    .UseDistributedTM();
            }
        }
    }
}
