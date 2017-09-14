using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using Orleans.Transactions.Development;

namespace Orleans.Transactions.Tests
{
    public class MemoryTransactionsFixture : BaseTestClusterFixture
    {
        protected override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions();
            options.ClusterConfiguration.UseStartupType<Startup>();
            options.ClusterConfiguration.AddMemoryStorageProvider(TransactionTestConstants.TransactionStore);
            return new TestCluster(options);
        }

        public class Startup
        {
            public IServiceProvider ConfigureServices(IServiceCollection services)
            {
                services.UseInClusterTransactionManager(new TransactionsConfiguration());
                services.UseInMemoryTransactionLog();
                services.UseTransactionalState();
                return services.BuildServiceProvider();
            }
        }
    }
}
