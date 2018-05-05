using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.Tests
{
    [TestCategory("Transactions")]
    public class TransactionRecoveryMemoryTests : TestClusterPerTest
    {
        private readonly TransactionRecoveryTestsRunner testRunner;
        public TransactionRecoveryMemoryTests(ITestOutputHelper helper)
        {
            this.testRunner = new TransactionRecoveryTestsRunner(this.HostedCluster, helper);
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 5;
            builder.CreateSilo = AppDomainSiloHandle.Create;
            builder.AddSiloBuilderConfigurator<TransactionRecoveryTestsRunner.SiloBuilderConfigurator>();
            builder.AddSiloBuilderConfigurator<MemoryTransactionsFixture.SiloBuilderConfigurator>();
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, true)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, true)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain, true)]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, false)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, false)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain, false)]
        public Task TransactionWillRecoverAfterRandomSiloFailure(string transactionTestGrainClassName,
            bool killSiloWhichRunsTm)
        {
            return this.testRunner.TransactionWillRecoverAfterRandomSiloFailure(transactionTestGrainClassName,
                killSiloWhichRunsTm);
        }
    }
}
