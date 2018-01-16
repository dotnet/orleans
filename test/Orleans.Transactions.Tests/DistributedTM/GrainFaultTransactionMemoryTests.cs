using Xunit.Abstractions;
using Xunit;
using System;

namespace Orleans.Transactions.Tests.DistributedTM
{
    [TestCategory("BVT"), TestCategory("Transactions")]
    public class GrainFaultTransactionMemoryTests : GrainFaultTransactionTestRunner, IClassFixture<MemoryTransactionsFixture>
    {
        public GrainFaultTransactionMemoryTests(MemoryTransactionsFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
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
