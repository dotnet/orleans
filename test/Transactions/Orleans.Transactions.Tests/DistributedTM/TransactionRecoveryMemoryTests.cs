using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.Tests.DistributedTM
{
    [TestCategory("Transactions"), TestCategory("Functional")]
    public class TransactionRecoveryMemoryTests : TestClusterPerTest
    {
        private readonly TransactionRecoveryTestsRunner testRunner;

        public TransactionRecoveryMemoryTests(ITestOutputHelper helper)
        {
            this.testRunner = new TransactionRecoveryTestsRunner(this.HostedCluster, helper, true);
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 5;
            builder.AddSiloBuilderConfigurator<Tests.DistributedTM.MemoryTransactionsFixture.SiloBuilderConfigurator>();
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.TransactionGrainStates.SingleStateTransaction)]
        [InlineData(TransactionTestConstants.TransactionGrainStates.DoubleStateTransaction)]
        [InlineData(TransactionTestConstants.TransactionGrainStates.MaxStateTransaction)]
        public Task TransactionWillRecoverAfterRandomSiloFailure(TransactionTestConstants.TransactionGrainStates transactionGrainStates)
        {
            return this.testRunner.TransactionWillRecoverAfterRandomSiloFailure(transactionGrainStates);
        }
    }
}
