using Orleans.Runtime;
using Orleans.Hosting;
using Orleans.TestingHost;
using Orleans.Transactions.Tests;
using Orleans.Transactions.Tests.DeactivationTransaction;
using TestExtensions;
using Tester;

namespace Orleans.Transactions.AzureStorage.Tests
{
    public class TestFixture : BaseTestClusterFixture
    {
        protected override void CheckPreconditionsOrThrow()
        {
            base.CheckPreconditionsOrThrow();
            TestUtils.CheckForAzureStorage();
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
        }

        public class SiloBuilderConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder
                    .ConfigureServices(services => services.AddSingletonNamedService<IRemoteCommitService, RemoteCommitService>(TransactionTestConstants.RemoteCommitService))
                    .ConfigureTracingForTransactionTests()
                    .AddAzureTableTransactionalStateStorage(TransactionTestConstants.TransactionStore, options =>
                    {
                        options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                    })
                    .UseDistributedTM();
            }
        }
    }

    public class DeactivationTestFixture : BaseTestClusterFixture
    {
        protected override void CheckPreconditionsOrThrow()
        {
            base.CheckPreconditionsOrThrow();
            TestUtils.CheckForAzureStorage();
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
        }

        public class SiloBuilderConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder
                    .ConfigureTracingForTransactionTests()
                    .AddAzureTableTransactionalStateStorage(TransactionTestConstants.TransactionStore, options =>
                    {
                        options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                    })
                    .UseDeactivationTransactionState()
                    .UseDistributedTM();
            }
        }
    }

    public class SkewedClockTestFixture : TestFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<SkewedClockConfigurator>();
            base.ConfigureTestCluster(builder);
        }
    }
}
