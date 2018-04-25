using Xunit;
using Xunit.Abstractions;
using Orleans.Transactions.Tests;
using System;

namespace Orleans.Transactions.AzureStorage.Tests.DistributedTM
{
    [TestCategory("Azure"), TestCategory("Transactions"), TestCategory("Functional")]
    public class GoldenPathTests : GoldenPathTransactionTestRunner, IClassFixture<TestFixture>
    {
        public GoldenPathTests(TestFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
            fixture.EnsurePreconditionsMet();
        }

        // change class name for test grains to use the correct grain class for distributed TM
        protected override ITransactionTestGrain TestGrain(string transactionTestGrainClassName, Guid id)
        {
            if (transactionTestGrainClassName == TransactionTestConstants.SingleStateTransactionalGrain)
                transactionTestGrainClassName = TransactionTestConstants.SingleStateTransactionalGrainDistributedTM;

            return base.TestGrain(transactionTestGrainClassName, id);
        }
    }
}
