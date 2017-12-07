using Xunit.Abstractions;
using Xunit;

namespace Orleans.Transactions.Tests
{
    [TestCategory("BVT"), TestCategory("Transactions")]
    public class GoldenPathTransactionMemoryTests : GoldenPathTransactionTestRunner, IClassFixture<MemoryTransactionsFixture>
    {
        public GoldenPathTransactionMemoryTests(MemoryTransactionsFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
        }
    }
}
