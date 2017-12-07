using Xunit.Abstractions;
using Xunit;

namespace Orleans.Transactions.Tests
{
    [TestCategory("BVT"), TestCategory("Transactions")]
    public class GrainFaultTransactionMemoryTests : GrainFaultTransactionTestRunner, IClassFixture<MemoryTransactionsFixture>
    {
        public GrainFaultTransactionMemoryTests(MemoryTransactionsFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
        }
    }
}
