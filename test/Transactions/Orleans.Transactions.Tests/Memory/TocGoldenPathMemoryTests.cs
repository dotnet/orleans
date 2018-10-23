using Xunit.Abstractions;
using Xunit;

namespace Orleans.Transactions.Tests
{
    [TestCategory("BVT"), TestCategory("Transactions")]
    public class TocGoldenPathTransactionMemoryTests : TocGoldenPathTestRunner, IClassFixture<MemoryTransactionsFixture>
    {
        public TocGoldenPathTransactionMemoryTests(MemoryTransactionsFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
        }
    }
}
