using Xunit.Abstractions;
using Xunit;

namespace Orleans.Transactions.Tests
{
    [TestCategory("BVT"), TestCategory("Transactions")]
    public class SkewedClockGoldenPathTransactionMemoryTests : GoldenPathTransactionTestRunner, IClassFixture<SkewedClockMemoryTransactionsFixture>
    {
        public SkewedClockGoldenPathTransactionMemoryTests(SkewedClockMemoryTransactionsFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
        }
    }
}
