using System.Threading.Tasks;
using Orleans.TestingHost;
using Orleans.Transactions.AzureStorage.Tests;
using Orleans.Transactions.Tests;
using Tester;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.Azure.Tests
{
    [TestCategory("Transactions")]
    public class TransactionRecoveryTests : TestClusterPerTest
    {
        private readonly TransactionRecoveryTestsRunner testRunner;

        public TransactionRecoveryTests(ITestOutputHelper helper)
        {
            this.EnsurePreconditionsMet();
            this.testRunner = new TransactionRecoveryTestsRunner(this.HostedCluster, helper);
        }

        protected override void CheckPreconditionsOrThrow()
        {
            base.CheckPreconditionsOrThrow();
            TestUtils.CheckForAzureStorage();
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 5;
            builder.AddSiloBuilderConfigurator<TestFixture.SiloBuilderConfigurator>();
            builder.AddSiloBuilderConfigurator<TransactionRecoveryTestsRunner.SiloBuilderConfiguratorUsingAzureClustering>();
            builder.AddClientBuilderConfigurator<TransactionRecoveryTestsRunner.ClientBuilderConfiguratorUsingAzureClustering>();
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public Task TransactionWillRecoverAfterRandomSiloGracefulShutdown(string transactionTestGrainClassName)
        {
            return this.testRunner.TransactionWillRecoverAfterRandomSiloGracefulShutdown(transactionTestGrainClassName);
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public Task TransactionWillRecoverAfterRandomSiloUnGracefulShutdown(string transactionTestGrainClassName)
        {
            return this.testRunner.TransactionWillRecoverAfterRandomSiloUnGracefulShutdown(transactionTestGrainClassName);
        }
    }
}
