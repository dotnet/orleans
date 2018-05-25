using Xunit.Abstractions;
using Xunit;

namespace Orleans.Transactions.Tests
{

    [TestCategory("BVT"), TestCategory("Transactions")]
    public class TransactionConcurrencyTests : TransactionConcurrencyTestRunner, IClassFixture<MemoryTransactionsFixture>
    {
        public TransactionConcurrencyTests(MemoryTransactionsFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
        }
    }
}
