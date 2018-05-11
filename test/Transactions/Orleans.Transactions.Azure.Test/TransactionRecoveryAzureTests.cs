using System.Threading.Tasks;
using Orleans.TestingHost;
using Orleans.Transactions.AzureStorage.Tests;
using Orleans.Transactions.Tests;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.Azure.Tests
{
    [TestCategory("Transactions")]
    public class TransactionRecoveryAzureTests : TestClusterPerTest
    {
        private readonly TransactionRecoveryTestsRunner testRunner;
        public TransactionRecoveryAzureTests(ITestOutputHelper helper)
        {
            this.testRunner = new TransactionRecoveryTestsRunner(this.HostedCluster, helper);
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 5;
            builder.CreateSilo = AppDomainSiloHandle.Create;
            builder.AddSiloBuilderConfigurator<TransactionRecoveryTestsRunner.SiloBuilderConfigurator>();
            builder.AddSiloBuilderConfigurator<TestFixture.SiloBuilderConfigurator>();
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public Task TransactionWillRecoverAfterRandomSiloFailure(string transactionTestGrainClassName)
        {
            return this.testRunner.TransactionWillRecoverAfterRandomSiloFailure(transactionTestGrainClassName);
        }
    }
}
