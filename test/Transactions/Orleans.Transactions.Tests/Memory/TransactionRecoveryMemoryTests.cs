using System.Threading.Tasks;
using Orleans.TestingHost;
using Tester;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.Tests
{
    [TestCategory("Transactions"), TestCategory("Functional")]
    public class TransactionRecoveryMemoryTests : TestClusterPerTest
    {
        protected override void CheckPreconditionsOrThrow()
        {
            base.CheckPreconditionsOrThrow();
            TestUtils.CheckForAzureStorage();
        }

        private readonly TransactionRecoveryTestsRunner testRunner;
        public TransactionRecoveryMemoryTests(ITestOutputHelper helper)
        {
            this.EnsurePreconditionsMet();
            this.testRunner = new TransactionRecoveryTestsRunner(this.HostedCluster, helper);
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 5;
            builder.CreateSilo = AppDomainSiloHandle.Create;
            builder.AddSiloBuilderConfigurator<MemoryTransactionsFixture.SiloBuilderConfigurator>();
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
