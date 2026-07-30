using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.Tests
{
    [TestCategory("BVT"), TestCategory("Transactions")]
    public class TransactionAttributionTest : TransactionAttributionTestRunner, IClassFixture<MemoryTransactionsFixture>
    {
        public TransactionAttributionTest(MemoryTransactionsFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
        }
    }
}
