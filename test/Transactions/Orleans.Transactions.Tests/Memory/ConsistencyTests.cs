using Xunit.Abstractions;
using Xunit;

namespace Orleans.Transactions.Tests
{
    [TestCategory("Transactions-dev")]
    public class ConsistencyTests : ConsistencyTransactionTestRunner, IClassFixture<MemoryTransactionsFixture>
    {
        public ConsistencyTests(MemoryTransactionsFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
        }

        protected override bool StorageErrorInjectionActive => false;
        protected override bool StorageAdaptorHasLimitedCommitSpace => false;

    }

}
