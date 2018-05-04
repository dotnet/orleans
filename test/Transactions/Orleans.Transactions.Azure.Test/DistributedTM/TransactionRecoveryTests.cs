using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.TestingHost;
using Orleans.Transactions.Tests;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.AzureStorage.Tests.DistributedTM
{
    [TestCategory("Transactions"), TestCategory("Functional")]
    public class TransactionRecoveryTests : TestClusterPerTest
    {
        private readonly TransactionRecoveryTestsRunner testRunner;

        public TransactionRecoveryTests(ITestOutputHelper helper)
        {
            this.testRunner = new TransactionRecoveryTestsRunner(this.HostedCluster, helper, true);
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            TestFixture.CheckForAzureStorage(TestDefaultConfiguration.DataConnectionString);
            builder.Options.InitialSilosCount = 5;
            builder.AddSiloBuilderConfigurator<TestFixture.SiloBuilderConfigurator>();
        }

        [SkippableTheory(Skip = "Intermittent failure, investigating...")]
        [InlineData(TransactionTestConstants.TransactionGrainStates.SingleStateTransaction)]
        [InlineData(TransactionTestConstants.TransactionGrainStates.DoubleStateTransaction)]
        [InlineData(TransactionTestConstants.TransactionGrainStates.MaxStateTransaction)]
        public Task TransactionWillRecoverAfterRandomSiloFailure(TransactionTestConstants.TransactionGrainStates transactionGrainStates)
        {
            return this.testRunner.TransactionWillRecoverAfterRandomSiloFailure(transactionGrainStates);
        }
    }
}
